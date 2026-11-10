using System.IdentityModel.Tokens.Jwt;
using System.Runtime.InteropServices;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
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
// COM refuses with its own sentence (bad password policy, duplicate name...): pass it on as a 400.
api.AddEndpointFilter(async (ctx, next) =>
{
    try { return await next(ctx); }
    catch (COMException e) { return HMail.Bad(e.Message); }
    catch (UnauthorizedAccessException) { return Results.Unauthorized(); }
    catch (Exception e)
    {
        ctx.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("api")
            .LogError(e, "Request {Path} failed", ctx.HttpContext.Request.Path);
        return Results.Problem(e.Message, statusCode: 500);   // admin-only API: the real reason helps debugging
    }
});

app.MapPost("/api/auth/login", (LoginRequest req, ILogger<Program> log) =>
{
    dynamic hm = HMail.Raw();
    dynamic? acc = hm.Authenticate(req.Username, req.Password);   // null when credentials are wrong
    if (acc is null)
    {
        log.LogWarning("Login '{User}' refused: hMailServer rejected the credentials.", req.Username);
        return Results.Unauthorized();
    }
    // The built-in Administrator signs in as server administrator by definition; mailbox accounts need AdminLevel 2.
    bool builtIn = string.Equals(req.Username, "Administrator", StringComparison.OrdinalIgnoreCase);
    int level = builtIn ? 2 : (int)acc.AdminLevel;
    if (level != 2)
    {
        log.LogWarning("Login '{User}' refused: AdminLevel is {Level}, server administrator (2) required.", req.Username, level);
        return Results.Unauthorized();
    }
    HMail.SetCredentials(req.Username, req.Password);
    var jwt = new JwtSecurityToken(
        claims: new[] { new Claim(ClaimTypes.Name, req.Username) },
        expires: DateTime.UtcNow.AddHours(8), signingCredentials: signing);
    return Results.Ok(new { token = new JwtSecurityTokenHandler().WriteToken(jwt) });
}).RequireRateLimiting("login");

api.MapGet("/stats", (SessionSampler sampler, ILogger<Program> log) =>
{
    dynamic st = HMail.Connect().Status;
    // Each counter is read on its own so one unsupported property cannot take the whole dashboard down.
    int Num(string name, Func<object?> read)
    {
        try { return Convert.ToInt32(read()); }
        catch (Exception e) { log.LogWarning("Status.{Name} unavailable: {Msg}", name, e.Message); return 0; }
    }
    int queue = 0;
    try { queue = HMail.QueueRows().Count; }
    catch (Exception e) { log.LogWarning("Queue count unavailable: {Msg}", e.Message); }
    return new
    {
        sessions = sampler.Snapshot(),
        queue,
        messages = new
        {
            total = Num("ProcessedMessages", () => (object?)st.ProcessedMessages),
            spam = Num("SpamMessages", () => (object?)st.SpamMessages),
            virus = Num("VirusMessages", () => (object?)st.VirusMessages),
        },
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

api.MapPost("/domains", (DomainInput i) =>
{
    if (!HMail.ValidDomain(i.Name)) return HMail.Bad("Tên miền không hợp lệ.");
    dynamic d = HMail.Connect().Domains.Add();
    d.Name = i.Name; d.Active = i.Active; d.MaxSize = i.QuotaMb; d.Save();
    return Results.NoContent();
});
api.MapPut("/domains/{id:int}", (int id, DomainInput i) =>
{
    if (!HMail.ValidDomain(i.Name)) return HMail.Bad("Tên miền không hợp lệ.");
    dynamic? d = HMail.Find(HMail.Connect().Domains, id);
    if (d is null) return Results.NotFound();
    d.Name = i.Name; d.Active = i.Active; d.MaxSize = i.QuotaMb; d.Save();
    return Results.NoContent();
});
api.MapDelete("/domains/{id:int}", (int id) =>
{
    dynamic? d = HMail.Find(HMail.Connect().Domains, id);
    if (d is null) return Results.NotFound();
    d.Delete();
    return Results.NoContent();
});

api.MapPost("/accounts", (AccountInput i) =>
{
    if (!HMail.ValidEmail(i.Address)) return HMail.Bad("Địa chỉ email không hợp lệ.");
    if ((i.Password ?? "").Length < 8) return HMail.Bad("Mật khẩu tối thiểu 8 ký tự.");
    dynamic? d = HMail.DomainOf(HMail.Connect(), i.Address);
    if (d is null) return HMail.Bad("Tên miền của địa chỉ này chưa tồn tại.");
    dynamic a = d.Accounts.Add();
    a.Address = i.Address; a.Password = i.Password; a.Active = i.Active; a.MaxSize = i.QuotaMb; a.Save();
    return Results.NoContent();
});
api.MapPut("/accounts/{id:int}", (int id, AccountInput i) =>
{
    dynamic? a = HMail.FindIn(HMail.Connect(), "Accounts", id);
    if (a is null) return Results.NotFound();
    if (!string.IsNullOrEmpty(i.Password))
    {
        if (i.Password.Length < 8) return HMail.Bad("Mật khẩu tối thiểu 8 ký tự.");
        a.Password = i.Password;
    }
    a.Active = i.Active; a.MaxSize = i.QuotaMb; a.Save();   // the address is not changed here
    return Results.NoContent();
});
api.MapDelete("/accounts/{id:int}", (int id) =>
{
    dynamic? a = HMail.FindIn(HMail.Connect(), "Accounts", id);
    if (a is null) return Results.NotFound();
    a.Delete();
    return Results.NoContent();
});

api.MapGet("/aliases", () =>
{
    dynamic hm = HMail.Connect();
    var list = new List<object>();
    for (int i = 0; i < (int)hm.Domains.Count; i++)
    {
        dynamic aliases = hm.Domains.Item[i].Aliases;
        for (int j = 0; j < (int)aliases.Count; j++)
        {
            dynamic a = aliases.Item[j];
            list.Add(new { id = (int)a.ID, name = (string)a.Name, value = (string)a.Value, active = (bool)a.Active });
        }
    }
    return list;
});
api.MapPost("/aliases", (AliasInput i) =>
{
    if (!HMail.ValidEmail(i.Name) || !HMail.ValidEmail(i.Value)) return HMail.Bad("Địa chỉ alias hoặc địa chỉ đích không hợp lệ.");
    dynamic? d = HMail.DomainOf(HMail.Connect(), i.Name);
    if (d is null) return HMail.Bad("Tên miền của alias chưa tồn tại.");
    dynamic a = d.Aliases.Add();
    a.Name = i.Name; a.Value = i.Value; a.Active = i.Active; a.Save();
    return Results.NoContent();
});
api.MapPut("/aliases/{id:int}", (int id, AliasInput i) =>
{
    if (!HMail.ValidEmail(i.Name) || !HMail.ValidEmail(i.Value)) return HMail.Bad("Địa chỉ alias hoặc địa chỉ đích không hợp lệ.");
    dynamic? a = HMail.FindIn(HMail.Connect(), "Aliases", id);
    if (a is null) return Results.NotFound();
    a.Name = i.Name; a.Value = i.Value; a.Active = i.Active; a.Save();
    return Results.NoContent();
});
api.MapDelete("/aliases/{id:int}", (int id) =>
{
    dynamic? a = HMail.FindIn(HMail.Connect(), "Aliases", id);
    if (a is null) return Results.NotFound();
    a.Delete();
    return Results.NoContent();
});

api.MapGet("/lists", () =>
{
    dynamic hm = HMail.Connect();
    var list = new List<object>();
    for (int i = 0; i < (int)hm.Domains.Count; i++)
    {
        dynamic dls = hm.Domains.Item[i].DistributionLists;
        for (int j = 0; j < (int)dls.Count; j++)
        {
            dynamic l = dls.Item[j];
            var members = new List<string>();
            for (int k = 0; k < (int)l.Recipients.Count; k++) members.Add((string)l.Recipients.Item[k].RecipientAddress);
            list.Add(new { id = (int)l.ID, address = (string)l.Address, active = (bool)l.Active, members });
        }
    }
    return list;
});
api.MapPost("/lists", (ListInput i) =>
{
    if (!HMail.ValidEmail(i.Address) || i.Members.Any(m => !HMail.ValidEmail(m))) return HMail.Bad("Có địa chỉ email không hợp lệ.");
    dynamic? d = HMail.DomainOf(HMail.Connect(), i.Address);
    if (d is null) return HMail.Bad("Tên miền của danh sách chưa tồn tại.");
    dynamic l = d.DistributionLists.Add();
    l.Address = i.Address; l.Active = i.Active; l.Save();   // save first so the list has an ID before members are added
    HMail.SyncMembers(l, i.Members);
    return Results.NoContent();
});
api.MapPut("/lists/{id:int}", (int id, ListInput i) =>
{
    if (i.Members.Any(m => !HMail.ValidEmail(m))) return HMail.Bad("Có địa chỉ email không hợp lệ.");
    dynamic? l = HMail.FindIn(HMail.Connect(), "DistributionLists", id);
    if (l is null) return Results.NotFound();
    l.Active = i.Active; l.Save();                            // the list address is not renamed here
    HMail.SyncMembers(l, i.Members);
    return Results.NoContent();
});
api.MapDelete("/lists/{id:int}", (int id) =>
{
    dynamic? l = HMail.FindIn(HMail.Connect(), "DistributionLists", id);
    if (l is null) return Results.NotFound();
    l.Delete();
    return Results.NoContent();
});

api.MapGet("/ranges", () =>
{
    dynamic rs = HMail.Connect().Settings.SecurityRanges;
    var list = new List<object>();
    for (int i = 0; i < (int)rs.Count; i++)
    {
        dynamic r = rs.Item[i];
        list.Add(new { id = (int)r.ID, name = (string)r.Name, lowerIp = (string)r.LowerIP, upperIp = (string)r.UpperIP, priority = (int)r.Priority,
                       allowSmtp = (bool)r.AllowSMTPConnections, allowPop = (bool)r.AllowPOP3Connections, allowImap = (bool)r.AllowIMAPConnections,
                       expires = (bool)r.Expires ? HMail.FormatDate(r.ExpiresTime) : null });
    }
    return list;
});
api.MapPost("/ranges", (RangeInput i) =>
{
    if (HMail.CheckRange(i) is { } err) return HMail.Bad(err);
    dynamic r = HMail.Connect().Settings.SecurityRanges.Add();
    HMail.FillRange(r, i); r.Save();
    return Results.NoContent();
});
api.MapPut("/ranges/{id:int}", (int id, RangeInput i) =>
{
    if (HMail.CheckRange(i) is { } err) return HMail.Bad(err);
    dynamic? r = HMail.Find(HMail.Connect().Settings.SecurityRanges, id);
    if (r is null) return Results.NotFound();
    HMail.FillRange(r, i); r.Save();
    return Results.NoContent();
});
api.MapDelete("/ranges/{id:int}", (int id) =>
{
    dynamic? r = HMail.Find(HMail.Connect().Settings.SecurityRanges, id);
    if (r is null) return Results.NotFound();
    r.Delete();
    return Results.NoContent();
});

api.MapGet("/settings/security", () =>
{
    dynamic s = HMail.Connect().Settings; dynamic g = s.AntiSpam;
    return new
    {
        autoBan = (bool)s.AutoBanOnLogonFailure, maxAttempts = (int)s.MaxInvalidLogonAttempts,
        withinMinutes = (int)s.MaxInvalidLogonAttemptsWithin, banMinutes = (int)s.AutoBanMinutes,
        greyEnabled = (bool)g.GreyListingEnabled, greyInitialDelayMin = (int)g.GreyListingInitialDelay,
        greyInitialDeleteHours = (int)g.GreyListingInitialDelete, greyFinalDeleteHours = (int)g.GreyListingFinalDelete,
    };
});
api.MapPut("/settings/security", (SecurityInput i) =>
{
    dynamic s = HMail.Connect().Settings; dynamic g = s.AntiSpam;
    s.AutoBanOnLogonFailure = i.AutoBan; s.MaxInvalidLogonAttempts = i.MaxAttempts;
    s.MaxInvalidLogonAttemptsWithin = i.WithinMinutes; s.AutoBanMinutes = i.BanMinutes;
    g.GreyListingEnabled = i.GreyEnabled; g.GreyListingInitialDelay = i.GreyInitialDelayMin;
    g.GreyListingInitialDelete = i.GreyInitialDeleteHours; g.GreyListingFinalDelete = i.GreyFinalDeleteHours;
    return Results.NoContent();
});

app.MapFallbackToFile("index.html");
app.Run();

record LoginRequest(string Username, string Password);
record DomainInput(string Name, bool Active, int QuotaMb);
record AccountInput(string Address, string? Password, bool Active, int QuotaMb);
record AliasInput(string Name, string Value, bool Active);
record ListInput(string Address, bool Active, string[] Members);
record RangeInput(string Name, string LowerIp, string UpperIp, int Priority, bool AllowSmtp, bool AllowPop, bool AllowImap);
record SecurityInput(bool AutoBan, int MaxAttempts, int WithinMinutes, int BanMinutes, bool GreyEnabled, int GreyInitialDelayMin, int GreyInitialDeleteHours, int GreyFinalDeleteHours);

static class HMail
{
    // Late binding: no interop assembly needed. Requires the bridge to run on the hMailServer host.
    public static dynamic Raw() =>
        Activator.CreateInstance(Type.GetTypeFromProgID("hMailServer.Application")
            ?? throw new InvalidOperationException("hMailServer COM is not registered on this machine."))!;

    // Every new COM Application object starts unauthenticated, so each call signs in again with the
    // credentials of the last successful login. They are kept in memory only (never written to disk).
    static (string User, string Pass)? _creds;
    public static void SetCredentials(string user, string pass) => _creds = (user, pass);

    public static dynamic Connect()
    {
        var c = _creds ?? throw new UnauthorizedAccessException("Chưa đăng nhập, hoặc bridge vừa khởi động lại.");
        dynamic app = Raw();
        if (app.Authenticate(c.User, c.Pass) is null)
        {
            _creds = null;
            throw new UnauthorizedAccessException("hMailServer không còn chấp nhận thông tin đăng nhập đã lưu.");
        }
        return app;
    }

    public static IResult Bad(string m) => Results.Problem(m, statusCode: 400);
    public static bool ValidEmail(string? s) => s is not null && System.Text.RegularExpressions.Regex.IsMatch(s, @"^[^@\s]+@[^@\s]+\.[^@\s]+$");
    public static bool ValidDomain(string? s) => s is not null && System.Text.RegularExpressions.Regex.IsMatch(s, @"^[a-z0-9-]+(\.[a-z0-9-]+)+$");

    // hMailServer collections are 0-based; items expose ID, Save() and Delete().
    public static dynamic? Find(dynamic col, int id)
    {
        for (int i = 0; i < (int)col.Count; i++) { dynamic it = col.Item[i]; if ((int)it.ID == id) return it; }
        return null;
    }
    public static dynamic? FindIn(dynamic hm, string child, int id)   // child = "Accounts" | "Aliases" | "DistributionLists"
    {
        for (int i = 0; i < (int)hm.Domains.Count; i++)
        {
            dynamic d = hm.Domains.Item[i];
            dynamic coll = child == "Accounts" ? d.Accounts : child == "Aliases" ? d.Aliases : d.DistributionLists;
            dynamic? hit = Find(coll, id);
            if (hit is not null) return hit;
        }
        return null;
    }
    public static dynamic? DomainOf(dynamic hm, string address)
    {
        string name = address[(address.IndexOf('@') + 1)..];
        for (int i = 0; i < (int)hm.Domains.Count; i++)
        {
            dynamic d = hm.Domains.Item[i];
            if (string.Equals((string)d.Name, name, StringComparison.OrdinalIgnoreCase)) return d;
        }
        return null;
    }

    public static void SyncMembers(dynamic list, IEnumerable<string> want)
    {
        var w = want.Select(x => x.Trim().ToLowerInvariant()).Where(x => x.Length > 0).ToHashSet();
        dynamic rec = list.Recipients;
        for (int k = (int)rec.Count - 1; k >= 0; k--)
        {
            dynamic r = rec.Item[k];
            if (!w.Remove(((string)r.RecipientAddress).ToLowerInvariant())) r.Delete();
        }
        foreach (var addr in w) { dynamic r = rec.Add(); r.RecipientAddress = addr; r.Save(); }
    }

    public static string? CheckRange(RangeInput i)
    {
        if (string.IsNullOrWhiteSpace(i.Name)) return "Tên không được để trống.";
        if (!System.Net.IPAddress.TryParse(i.LowerIp, out var lo) || !System.Net.IPAddress.TryParse(i.UpperIp, out var hi)) return "Địa chỉ IP không hợp lệ.";
        if (lo.AddressFamily != hi.AddressFamily) return "IP bắt đầu và IP kết thúc phải cùng loại (IPv4 hoặc IPv6).";
        return null;
    }
    public static void FillRange(dynamic r, RangeInput i)
    {
        r.Name = i.Name; r.LowerIP = i.LowerIp; r.UpperIP = i.UpperIp; r.Priority = i.Priority;
        r.AllowSMTPConnections = i.AllowSmtp; r.AllowPOP3Connections = i.AllowPop; r.AllowIMAPConnections = i.AllowImap;
    }

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
sealed class SessionSampler(ILogger<SessionSampler> log) : BackgroundService
{
    readonly Queue<int> _points = new();
    bool _warned;
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
            catch (UnauthorizedAccessException) { /* nobody has logged in yet */ }
            catch (Exception e)
            {
                if (!_warned) { _warned = true; log.LogWarning("Session sampling failed, chart stays empty: {Msg}", e.Message); }
            }
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
        }
    }
}
