using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

var b = WebApplication.CreateBuilder(args);

// Secret comes from env var Jwt__Key (>= 32 chars). Never commit it.
var secret = b.Configuration["Jwt:Key"];
if (string.IsNullOrEmpty(secret) || secret.Length < 32)
    throw new InvalidOperationException("Set Jwt__Key (>= 32 chars) as an environment variable.");
var signing = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)), SecurityAlgorithms.HmacSha256);

b.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
    o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = false, ValidateAudience = false,
        IssuerSigningKey = signing.Key, ClockSkew = TimeSpan.FromSeconds(30),
    });
b.Services.AddAuthorization();
b.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.AddFixedWindowLimiter("login", l => { l.PermitLimit = 5; l.Window = TimeSpan.FromMinutes(1); });
});
b.Services.AddSingleton<SessionSampler>();
b.Services.AddHostedService(sp => sp.GetRequiredService<SessionSampler>());

var app = b.Build();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseDefaultFiles();
app.UseStaticFiles(); // put the Vite build (dist/*) into wwwroot to serve the SPA from the same origin

var api = app.MapGroup("/api").RequireAuthorization();

app.MapPost("/api/auth/login", (LoginRequest req) =>
{
    dynamic hm = HMail.Connect();
    dynamic? acc = hm.Authenticate(req.Username, req.Password);   // null when credentials are wrong
    if (acc is null || (int)acc.AdminLevel != 2) return Results.Unauthorized(); // 2 = server administrator
    var jwt = new JwtSecurityToken(
        claims: new[] { new Claim(ClaimTypes.Name, req.Username) },
        expires: DateTime.UtcNow.AddHours(8), signingCredentials: signing);
    return Results.Ok(new { token = new JwtSecurityTokenHandler().WriteToken(jwt) });
}).RequireRateLimiting("login");

api.MapGet("/stats", (SessionSampler sampler) =>
{
    dynamic st = HMail.Connect().Status;
    return new
    {
        sessions = sampler.Snapshot(),
        queue = HMail.QueueRows().Count,
        messages = new { total = (int)st.ProcessedMessages, spam = (int)st.SpamMessages, virus = (int)st.VirusMessages },
    };
});

api.MapGet("/domains", () =>
{
    dynamic hm = HMail.Connect();
    var list = new List<object>();
    for (int i = 0; i < (int)hm.Domains.Count; i++)
    {
        dynamic d = hm.Domains.Item[i];
        dynamic accounts = d.Accounts;
        double used = 0;
        for (int j = 0; j < (int)accounts.Count; j++) used += (double)accounts.Item[j].Size;
        list.Add(new { id = (int)d.ID, name = (string)d.Name, active = (bool)d.Active,
                       accounts = (int)accounts.Count, quotaMb = (int)d.MaxSize, usedMb = (int)Math.Round(used) });
    }
    return list;
});

api.MapGet("/accounts", () =>
{
    dynamic hm = HMail.Connect();
    var list = new List<object>();
    for (int i = 0; i < (int)hm.Domains.Count; i++)
    {
        dynamic accounts = hm.Domains.Item[i].Accounts;
        for (int j = 0; j < (int)accounts.Count; j++)
        {
            dynamic a = accounts.Item[j];
            list.Add(new { id = (int)a.ID, address = (string)a.Address, active = (bool)a.Active,
                           quotaMb = (int)a.MaxSize, usedMb = (int)Math.Round((double)a.Size),
                           lastLogon = HMail.FormatDate(a.LastLogonTime) });
        }
    }
    return list;
});

api.MapGet("/queue", () => HMail.QueueRows());

// Handy while verifying the column layout of Status.UndeliveredMessages on your hMailServer version.
api.MapGet("/queue/raw", () => Results.Text((string)HMail.Connect().Status.UndeliveredMessages));

api.MapPost("/queue/retry", () =>
{
    dynamic q = HMail.Connect().GlobalObjects.DeliveryQueue;
    q.ResetDeliveryTime();
    q.StartDelivery();
    return Results.NoContent();
});

// Removing ONE queued message is not implemented: I could not confirm a per-message COM call.
// Check the COM reference for your version (APIs Reference) before enabling this.
api.MapDelete("/queue/{id:int}", (int id) => Results.StatusCode(501));

app.MapFallbackToFile("index.html");
app.Run();

record LoginRequest(string Username, string Password);

static class HMail
{
    // Late binding: no interop assembly needed. Requires the bridge to run on the hMailServer host.
    public static object Connect() =>
        Activator.CreateInstance(Type.GetTypeFromProgID("hMailServer.Application")
            ?? throw new InvalidOperationException("hMailServer COM is not registered on this machine."))!;

    public static string FormatDate(object? v)
    {
        try { return Convert.ToDateTime(v).ToString("yyyy-MM-dd HH:mm"); } catch { return ""; }
    }

    // UndeliveredMessages is tab-separated text, one row per message. Column order is NOT confirmed:
    // call GET /api/queue/raw once and adjust these indexes.
    const int ColId = 0, ColFrom = 2, ColTo = 3, ColCreated = 1, ColRetries = 4;

    public static List<object> QueueRows()
    {
        string raw = (string)((dynamic)Connect()).Status.UndeliveredMessages;
        var rows = new List<object>();
        foreach (var line in raw.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            var c = line.Split('\t');
            string At(int i) => i < c.Length ? c[i] : "";
            rows.Add(new { id = int.TryParse(At(ColId), out var id) ? id : 0, from = At(ColFrom), to = At(ColTo),
                           queuedAt = At(ColCreated), retries = int.TryParse(At(ColRetries), out var r) ? r : 0 });
        }
        return rows;
    }
}

// Samples the number of open sessions every 30 s so the dashboard has a live line (last 24 samples).
sealed class SessionSampler : BackgroundService
{
    readonly Queue<int> _points = new();
    public int[] Snapshot() { lock (_points) return _points.ToArray(); }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                dynamic st = HMail.Connect().Status;
                int n = (int)st.SessionCount(1) + (int)st.SessionCount(2) + (int)st.SessionCount(3); // SMTP, POP3, IMAP
                lock (_points) { _points.Enqueue(n); while (_points.Count > 24) _points.Dequeue(); }
            }
            catch { /* hMailServer stopped or COM unavailable: skip this sample */ }
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
        }
    }
}
