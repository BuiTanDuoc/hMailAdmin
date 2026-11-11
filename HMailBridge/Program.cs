using System.IdentityModel.Tokens.Jwt;
using System.Runtime.InteropServices;
using System.Security.Claims;
using DnsClient;
using System.Formats.Asn1;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
            spam = Num("RemovedSpamMessages", () => (object?)st.RemovedSpamMessages),
            virus = Num("RemovedViruses", () => (object?)st.RemovedViruses),
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
    foreach (var r in HMail.QueueRows()) q.ResetDeliveryTime(r.Id);   // ResetDeliveryTime takes a message ID
    q.StartDelivery();
    return Results.NoContent();
});
api.MapPost("/queue/{id:int}/retry", (int id) =>
{
    if (!HMail.QueueRows().Any(r => r.Id == id)) return Results.NotFound();
    dynamic q = HMail.Connect().GlobalObjects.DeliveryQueue;
    q.ResetDeliveryTime(id); q.StartDelivery();
    return Results.NoContent();
});
// Only messages that hMailServer itself lists in the queue can be read: the file path never comes from the client.
api.MapGet("/queue/{id:int}/source", (int id) =>
{
    var row = HMail.QueueRows().FirstOrDefault(r => r.Id == id);
    if (row is null) return Results.NotFound();
    string path = row.FileName;
    if (!Path.IsPathRooted(path))
    {
        try { path = Path.Combine((string)HMail.Connect().Settings.Directories.DataDirectory, path); }
        catch { return HMail.Bad("Không xác định được thư mục Data của hMailServer."); }
    }
    if (!path.EndsWith(".eml", StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return HMail.Bad($"Không tìm thấy file thư trên máy chủ: {path}");
    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    var buf = new byte[Math.Min(fs.Length, 262144)];
    fs.ReadExactly(buf);
    return Results.Ok(new { id, source = Encoding.UTF8.GetString(buf), truncated = fs.Length > buf.Length });
});

api.MapDelete("/queue/{id:int}", (int id) =>
{
    if (!HMail.QueueRows().Any(r => r.Id == id)) return Results.NotFound();
    HMail.Connect().GlobalObjects.DeliveryQueue.Remove(id);   // the "Remove" action of the Administrator's queue list
    return Results.NoContent();
});

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

api.MapGet("/logs", (IConfiguration cfg) =>
{
    var dir = LogStore.Dir(cfg);
    if (!Directory.Exists(dir)) return Results.Problem($"Không tìm thấy thư mục log: {dir}. Đặt Logs:Directory trong appsettings.json.", statusCode: 404);
    return Results.Ok(new DirectoryInfo(dir).GetFiles("*.log").Where(f => LogStore.NameOk(f.Name))
        .OrderByDescending(f => f.LastWriteTime)
        .Select(f => new { name = f.Name, size = f.Length, modified = f.LastWriteTime.ToString("yyyy-MM-dd HH:mm") }));
});

api.MapGet("/logs/{name}/summary", (string name, IConfiguration cfg) =>
{
    if (!LogStore.NameOk(name)) return HMail.Bad("Tên file log không hợp lệ.");
    var path = Path.Combine(LogStore.Dir(cfg), name);
    if (!File.Exists(path)) return Results.NotFound();
    int lines = 0, deliveries = 0;
    var byType = new Dictionary<string, int>();
    var ips = new Dictionary<string, int>();
    var fails = new Dictionary<(string Proto, string Ip), (int Count, string Last)>();
    var errs = new Dictionary<string, (string Sev, int Count, string Sample)>();
    var errIps = new Dictionary<string, int>();
    foreach (var l in LogStore.Read(path))
    {
        lines++;
        byType[l.Type] = byType.GetValueOrDefault(l.Type) + 1;
        if (l.Ip is not null) ips[l.Ip] = ips.GetValueOrDefault(l.Ip) + 1;
        if (LogStore.IsFailedLogin(l)) { var k = (l.Type, l.Ip ?? ""); fails[k] = (fails.GetValueOrDefault(k).Count + 1, l.Time); }
        if (l.Type == "APPLICATION" && l.Message.Contains("Delivering message from")) deliveries++;
        if (l.Type == "ERROR")
        {
            var code = LogStore.CodeRx.Match(l.Message) is { Success: true } m ? m.Groups[1].Value : "?";
            var sev = LogStore.SevRx.Match(l.Message) is { Success: true } sm ? sm.Groups[1].Value : "";
            var cur = errs.GetValueOrDefault(code);
            errs[code] = (sev, cur.Count + 1, cur.Sample ?? (l.Message.Length > 240 ? l.Message[..240] + "…" : l.Message));
            if (LogStore.IpRx.Match(l.Message) is { Success: true } im) errIps[im.Groups[1].Value] = errIps.GetValueOrDefault(im.Groups[1].Value) + 1;
        }
    }
    return Results.Ok(new
    {
        lines, deliveries, byType,
        failedTotal = fails.Values.Sum(v => v.Count),
        errorsTotal = errs.Values.Sum(v => v.Count),
        failedLogins = fails.OrderByDescending(x => x.Value.Count).Take(20)
            .Select(x => new { ip = x.Key.Ip, protocol = x.Key.Proto, count = x.Value.Count, last = x.Value.Last }),
        topIps = ips.OrderByDescending(x => x.Value).Take(10).Select(x => new { ip = x.Key, count = x.Value }),
        errors = errs.OrderByDescending(x => x.Value.Count).Take(20)
            .Select(x => new { code = x.Key, severity = x.Value.Sev, count = x.Value.Count, sample = x.Value.Sample }),
        errorIps = errIps.OrderByDescending(x => x.Value).Take(10).Select(x => new { ip = x.Key, count = x.Value }),
    });
});

api.MapGet("/logs/{name}/search", (string name, string? q, string? type, string? ip, int? take, IConfiguration cfg) =>
{
    if (!LogStore.NameOk(name)) return HMail.Bad("Tên file log không hợp lệ.");
    var path = Path.Combine(LogStore.Dir(cfg), name);
    if (!File.Exists(path)) return Results.NotFound();
    int max = Math.Clamp(take ?? 200, 1, 1000);
    var buf = new Queue<LogLine>();   // keeps only the newest `max` matches
    foreach (var l in LogStore.Read(path))
    {
        if (!string.IsNullOrEmpty(type) && !string.Equals(l.Type, type, StringComparison.OrdinalIgnoreCase)) continue;
        if (!string.IsNullOrEmpty(ip) && l.Ip != ip && !l.Message.Contains(ip)) continue;
        if (!string.IsNullOrEmpty(q) && !l.Message.Contains(q, StringComparison.OrdinalIgnoreCase)) continue;
        buf.Enqueue(l);
        if (buf.Count > max) buf.Dequeue();
    }
    return Results.Ok(buf.Reverse().Select(l => new { type = l.Type, time = l.Time, ip = l.Ip, message = l.Message.Length > 600 ? l.Message[..600] + "…" : l.Message }));
});

// ---------- Rules (server-wide) ----------
api.MapGet("/rules", () =>
{
    dynamic rules = HMail.Connect().Rules;
    var list = new List<RuleDto>();
    for (int i = 0; i < (int)rules.Count; i++)
    {
        dynamic r = rules.Item[i];
        bool supported = true;
        var crit = new List<CritDto>();
        for (int j = 0; j < (int)r.Criterias.Count; j++)
        {
            dynamic c = r.Criterias.Item[j];
            bool predefined = (bool)c.UsePredefined;
            string? field = predefined ? RuleMap.Name(RuleMap.Fields, (int)c.PredefinedField) : "header";
            string? match = RuleMap.Name(RuleMap.Matches, (int)c.MatchType);
            if (field is null || match is null) supported = false;
            crit.Add(new CritDto(field ?? "?", predefined ? "" : (string)c.HeaderField, match ?? "?", (string)c.MatchValue));
        }
        var acts = new List<ActDto>();
        for (int j = 0; j < (int)r.Actions.Count; j++)
        {
            dynamic a = r.Actions.Item[j];
            string? type = RuleMap.Name(RuleMap.Actions, (int)a.Type);
            if (type is null) { supported = false; acts.Add(new ActDto("?", "", "", "", "")); continue; }
            acts.Add(new ActDto(type, type == "forward" ? (string)a.To : "", type == "move" ? (string)a.IMAPFolder : "",
                                type == "setheader" ? (string)a.HeaderName : "", type == "setheader" ? (string)a.Value : ""));
        }
        list.Add(new RuleDto((int)r.ID, (string)r.Name, (bool)r.Active, (bool)r.UseAND, supported, crit, acts));
    }
    return list;
});
api.MapPost("/rules", (RuleInput i) =>
{
    if (RuleMap.Check(i) is { } err) return HMail.Bad(err);
    RuleMap.Apply(HMail.Connect().Rules.Add(), i);
    return Results.NoContent();
});
api.MapPut("/rules/{id:int}", (int id, RuleInput i) =>
{
    if (RuleMap.Check(i) is { } err) return HMail.Bad(err);
    dynamic? r = HMail.Find(HMail.Connect().Rules, id);
    if (r is null) return Results.NotFound();
    RuleMap.Apply(r, i);   // criteria and actions are replaced as a whole
    return Results.NoContent();
});
api.MapDelete("/rules/{id:int}", (int id) =>
{
    dynamic? r = HMail.Find(HMail.Connect().Rules, id);
    if (r is null) return Results.NotFound();
    r.Delete();
    return Results.NoContent();
});
api.MapPost("/rules/{id:int}/move", (int id, string dir) =>
{
    dynamic? r = HMail.Find(HMail.Connect().Rules, id);
    if (r is null) return Results.NotFound();
    if (dir == "up") r.MoveUp(); else if (dir == "down") r.MoveDown(); else return HMail.Bad("dir phải là up hoặc down.");
    return Results.NoContent();
});

// ---------- Settings (whitelist; each key reads/writes on its own) ----------
api.MapGet("/settings", () =>
{
    object s = HMail.Connect().Settings;
    var rows = new List<SettingRow>();
    foreach (var d in SettingsMap.All)
    {
        try { var (h, p) = SettingsMap.Resolve(s, d.Key); rows.Add(new SettingRow(d.Group, d.Key, d.Label, d.Type, SettingsMap.Get(h, p), null)); }
        catch (Exception e) { rows.Add(new SettingRow(d.Group, d.Key, d.Label, d.Type, null, SettingsMap.Short(e))); }
    }
    return rows;
});
api.MapPut("/settings", (Dictionary<string, JsonElement> values) =>
{
    object s = HMail.Connect().Settings;
    var errors = new Dictionary<string, string>();
    foreach (var d in SettingsMap.All)
    {
        if (!values.TryGetValue(d.Key, out var v)) continue;
        try
        {
            var (h, p) = SettingsMap.Resolve(s, d.Key);
            object val = d.Type switch { "bool" => v.GetBoolean(), "int" => v.GetInt32(), _ => v.GetString() ?? "" };
            SettingsMap.Set(h, p, val);
        }
        catch (Exception e) { errors[d.Key] = SettingsMap.Short(e); }
    }
    return Results.Ok(new { errors });
});

// ---------- Utilities ----------
api.MapGet("/utilities/server", () => new { state = (int)HMail.Connect().ServerState });   // 1 stopped, 2 starting, 3 running, 4 stopping
api.MapPost("/utilities/server/{action}", (string action) =>
{
    dynamic hm = HMail.Connect();
    if (action == "start") hm.Start(); else if (action == "stop") hm.Stop(); else return HMail.Bad("action phải là start hoặc stop.");
    return Results.NoContent();
});
api.MapPost("/utilities/backup", () => { HMail.Connect().BackupManager.StartBackup(); return Results.NoContent(); });
api.MapPost("/utilities/blacklist", async (BlacklistRequest req) =>
{
    if (!IPAddress.TryParse(req.Ip, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork) return HMail.Bad("Chỉ hỗ trợ kiểm tra địa chỉ IPv4.");
    var rev = string.Join('.', ip.GetAddressBytes().Reverse());
    var results = await Task.WhenAll(Dnsbl.Zones.Select(z => Dnsbl.CheckAsync(rev, z)));
    return Results.Ok(results);
});

// ---------- Diagnostics per domain ----------
api.MapPost("/diagnostics", async (DiagRequest req) =>
{
    if (!HMail.ValidDomain(req.Domain)) return HMail.Bad("Tên miền không hợp lệ.");
    if (!string.IsNullOrWhiteSpace(req.ServerIp) && !IPAddress.TryParse(req.ServerIp, out _)) return HMail.Bad("IP máy chủ không hợp lệ.");
    if (!string.IsNullOrWhiteSpace(req.Selector) && !System.Text.RegularExpressions.Regex.IsMatch(req.Selector, @"^[A-Za-z0-9._-]{1,63}$")) return HMail.Bad("Selector DKIM không hợp lệ.");
    dynamic hm = HMail.Connect();
    dynamic? d = HMail.DomainOf(hm, "postmaster@" + req.Domain);
    if (d is null) return HMail.Bad("Chỉ chẩn đoán được tên miền đã có trong hMailServer.");   // also keeps this endpoint from probing arbitrary domains
    var checks = await Diag.RunAsync(req.Domain.ToLowerInvariant(), (bool)d.Active, req.ServerIp, req.Selector, (string)hm.Settings.HostName);
    return Results.Ok(checks);
});

// ---------- SSL certificates ----------
api.MapGet("/ssl", () =>
{
    dynamic hm = HMail.Connect();
    string host = (string)hm.Settings.HostName;
    dynamic col = hm.Settings.SSLCertificates;
    var list = new List<CertRow>();
    for (int i = 0; i < (int)col.Count; i++)
    {
        dynamic c = col.Item[i];
        string cf = (string)c.CertificateFile, kf = (string)c.PrivateKeyFile;
        list.Add(new CertRow((int)c.ID, (string)c.Name, cf, kf, CertCheck.Analyze(cf, kf, host)));
    }
    return list;
});
api.MapPost("/ssl", (CertInput i) =>
{
    if (string.IsNullOrWhiteSpace(i.Name) || string.IsNullOrWhiteSpace(i.CertificateFile) || string.IsNullOrWhiteSpace(i.PrivateKeyFile)) return HMail.Bad("Cần nhập tên, file chứng chỉ và file khoá riêng.");
    dynamic c = HMail.Connect().Settings.SSLCertificates.Add();
    c.Name = i.Name; c.CertificateFile = i.CertificateFile; c.PrivateKeyFile = i.PrivateKeyFile; c.Save();
    return Results.NoContent();
});
api.MapPut("/ssl/{id:int}", (int id, CertInput i) =>
{
    dynamic? c = HMail.Find(HMail.Connect().Settings.SSLCertificates, id);
    if (c is null) return Results.NotFound();
    c.Name = i.Name; c.CertificateFile = i.CertificateFile; c.PrivateKeyFile = i.PrivateKeyFile; c.Save();
    return Results.NoContent();
});
api.MapDelete("/ssl/{id:int}", (int id) =>
{
    dynamic? c = HMail.Find(HMail.Connect().Settings.SSLCertificates, id);
    if (c is null) return Results.NotFound();
    c.Delete();
    return Results.NoContent();
});

// ---------- Live logging: tail today's log file ----------
// Registered before /logs/{name}/... routes by being a distinct path (/logs/live).
api.MapGet("/logs/live", (string? file, long? offset, IConfiguration cfg) =>
{
    var cur = $"hmailserver_{DateTime.Now:yyyy-MM-dd}.log";
    var path = Path.Combine(LogStore.Dir(cfg), cur);
    if (!File.Exists(path))
        return Results.Ok(new LiveResp(cur, 0, new List<LogLine>(), "Chưa có file log hôm nay. Kiểm tra ghi log đã bật trong Cài đặt."));
    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    bool resume = file == cur && offset is >= 0 && offset <= fs.Length;   // otherwise: first call, new day, or file truncated
    long start = resume ? offset!.Value : Math.Max(0, fs.Length - 65536);
    int len = (int)Math.Min(fs.Length - start, 262144);
    var buf = new byte[len];
    fs.Seek(start, SeekOrigin.Begin);
    fs.ReadExactly(buf);
    int end = Array.LastIndexOf(buf, (byte)'\n');
    if (end < 0) return Results.Ok(new LiveResp(cur, len == 262144 ? start + len : start, new List<LogLine>(), null));   // no complete line yet (or one huge line: skip it)
    int begin = 0;
    if (!resume && start > 0) begin = Array.IndexOf(buf, (byte)'\n') + 1;   // drop the partial first line
    var text = Encoding.UTF8.GetString(buf, begin, Math.Max(0, end + 1 - begin));
    var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(x => LogStore.ParseLine(x.TrimEnd('\r'))).Where(x => x is not null).Select(x => x!).ToList();
    if (!resume && lines.Count > 100) lines = lines.GetRange(lines.Count - 100, 100);
    return Results.Ok(new LiveResp(cur, start + end + 1, lines, null));
});

// ---------- DNS blacklists (anti-spam) ----------
api.MapGet("/dnsbl", () =>
{
    dynamic col = HMail.Connect().Settings.AntiSpam.DNSBlackLists;
    var list = new List<DnsblDto>();
    for (int i = 0; i < (int)col.Count; i++)
    {
        dynamic b = col.Item[i];
        list.Add(new DnsblDto((int)b.ID, (string)b.DNSHost, (bool)b.Active, (int)b.Score, (string)b.ExpectedResult, (string)b.RejectMessage));
    }
    return list;
});
api.MapPost("/dnsbl", (DnsblInput i) =>
{
    if (HMail.CheckDnsbl(i) is { } err) return HMail.Bad(err);
    dynamic b = HMail.Connect().Settings.AntiSpam.DNSBlackLists.Add();
    HMail.FillDnsbl(b, i);
    return Results.NoContent();
});
api.MapPut("/dnsbl/{id:int}", (int id, DnsblInput i) =>
{
    if (HMail.CheckDnsbl(i) is { } err) return HMail.Bad(err);
    dynamic? b = HMail.Find(HMail.Connect().Settings.AntiSpam.DNSBlackLists, id);
    if (b is null) return Results.NotFound();
    HMail.FillDnsbl(b, i);
    return Results.NoContent();
});
api.MapDelete("/dnsbl/{id:int}", (int id) =>
{
    dynamic? b = HMail.Find(HMail.Connect().Settings.AntiSpam.DNSBlackLists, id);
    if (b is null) return Results.NotFound();
    b.Delete();
    return Results.NoContent();
});

// ---------- TCP/IP ports ----------
api.MapGet("/ports", async () =>
{
    dynamic col = HMail.Connect().Settings.TCPIPPorts;
    var rows = new List<PortDto>();
    for (int i = 0; i < (int)col.Count; i++)
    {
        dynamic p = col.Item[i];
        int cert = 0;
        try { cert = (int)p.SSLCertificateID; } catch { /* property missing on this version: show as none */ }
        rows.Add(new PortDto((int)p.ID, (int)p.Protocol, (string)p.Address, (int)p.PortNumber, (int)p.ConnectionSecurity, cert, false));
    }
    var probed = await Task.WhenAll(rows.Select(async r => r with { Listening = await HMail.ProbePortAsync(r.Address, r.Port) }));
    return Results.Ok(probed);
});
api.MapPost("/ports", (PortInput i) =>
{
    dynamic hm = HMail.Connect();
    if (HMail.CheckPort(hm, i, null) is { } err) return HMail.Bad(err);
    HMail.FillPort(hm.Settings.TCPIPPorts.Add(), i);
    return Results.NoContent();
});
api.MapPut("/ports/{id:int}", (int id, PortInput i) =>
{
    dynamic hm = HMail.Connect();
    if (HMail.CheckPort(hm, i, id) is { } err) return HMail.Bad(err);
    dynamic? p = HMail.Find(hm.Settings.TCPIPPorts, id);
    if (p is null) return Results.NotFound();
    HMail.FillPort(p, i);
    return Results.NoContent();
});
api.MapDelete("/ports/{id:int}", (int id) =>
{
    dynamic? p = HMail.Find(HMail.Connect().Settings.TCPIPPorts, id);
    if (p is null) return Results.NotFound();
    p.Delete();
    return Results.NoContent();
});

// ---------- Routes, incoming relays, SURBL servers, greylisting white list ----------
foreach (var def in Resources.All)
{
    var route = "/r/" + def.Name;
    api.MapGet(route, () => Resources.List(def));
    api.MapPost(route, (Dictionary<string, JsonElement> body) => Resources.Save(def, null, body));
    api.MapPut(route + "/{id:int}", (int id, Dictionary<string, JsonElement> body) => Resources.Save(def, id, body));
    api.MapDelete(route + "/{id:int}", (int id) => Resources.Remove(def, id));
}

// Unknown /api paths must answer 404 JSON, not the SPA's index.html (that made a missing endpoint look like a JSON parse error).
app.MapFallback("/api/{**path}", () => Results.Problem("API này chưa có trong bridge đang chạy. Hãy thay bản HMailBridge mới nhất và khởi động lại bridge.", statusCode: 404));
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

    public static string? CheckDnsbl(DnsblInput i)
    {
        if (!ValidDomain(i.Host?.Trim().ToLowerInvariant())) return "Tên máy chủ DNSBL không hợp lệ (vd. zen.spamhaus.org).";
        if (i.Score < 0 || i.Score > 100) return "Điểm phải từ 0 đến 100.";
        if (!string.IsNullOrWhiteSpace(i.ExpectedResult) && !System.Net.IPAddress.TryParse(i.ExpectedResult, out _)) return "Kết quả mong đợi phải là một địa chỉ IP (vd. 127.0.0.2) hoặc để trống.";
        return null;
    }
    public static void FillDnsbl(dynamic b, DnsblInput i)
    {
        b.DNSHost = i.Host.Trim().ToLowerInvariant(); b.Active = i.Active; b.Score = i.Score;
        b.ExpectedResult = i.ExpectedResult?.Trim() ?? ""; b.RejectMessage = i.RejectMessage ?? ""; b.Save();
    }

    // eSessionType: SMTP=1, POP3=3, IMAP=5. eConnectionSecurity: 0 none, 1 SSL/TLS (implicit), 2 STARTTLS optional, 3 STARTTLS required.
    public static string? CheckPort(dynamic hm, PortInput i, int? selfId)
    {
        if (i.Protocol is not (1 or 3 or 5)) return "Giao thức phải là SMTP, POP3 hoặc IMAP.";
        if (i.Port is < 1 or > 65535) return "Cổng phải từ 1 đến 65535.";
        if (!System.Net.IPAddress.TryParse(i.Address, out var addr)) return "Địa chỉ IP không hợp lệ (dùng 0.0.0.0 cho mọi địa chỉ).";
        if (i.Security is < 0 or > 3) return "Kiểu bảo mật không hợp lệ.";
        if (i.Security != 0 && (i.CertificateId <= 0 || Find(hm.Settings.SSLCertificates, i.CertificateId) is null))
            return "Kiểu bảo mật này cần chọn một chứng chỉ SSL hợp lệ.";
        dynamic col = hm.Settings.TCPIPPorts;
        for (int k = 0; k < (int)col.Count; k++)
        {
            dynamic p = col.Item[k];
            if (selfId is not null && (int)p.ID == selfId) continue;
            if ((int)p.PortNumber != i.Port || !System.Net.IPAddress.TryParse((string)p.Address, out var other) || other.AddressFamily != addr.AddressFamily) continue;
            bool wildcard = addr.Equals(System.Net.IPAddress.Any) || addr.Equals(System.Net.IPAddress.IPv6Any) || other.Equals(System.Net.IPAddress.Any) || other.Equals(System.Net.IPAddress.IPv6Any);
            if (wildcard || addr.Equals(other)) return $"Cổng {i.Port} đã được dùng tại địa chỉ {(string)p.Address}; hMailServer sẽ không bind được.";
        }
        return null;
    }
    public static void FillPort(dynamic p, PortInput i)
    {
        p.Protocol = i.Protocol; p.Address = i.Address.Trim(); p.PortNumber = i.Port;
        p.ConnectionSecurity = i.Security; p.SSLCertificateID = i.Security == 0 ? 0 : i.CertificateId; p.Save();
    }
    // Is something accepting connections on this port? Tested from the bridge, on the same machine.
    public static async Task<bool> ProbePortAsync(string address, int port)
    {
        try
        {
            var host = address is "" or "0.0.0.0" ? "127.0.0.1" : address == "::" ? "::1" : address;
            using var cts = new CancellationTokenSource(800);
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(host, port, cts.Token);
            return true;
        }
        catch { return false; }
    }

    public static string FormatDate(object? v)
    {
        try { return Convert.ToDateTime(v).ToString("yyyy-MM-dd HH:mm"); } catch { return ""; }
    }

    // Status.UndeliveredMessages is tab-separated, one row per message. Columns, taken from a working PowerShell
    // script on the hMailServer forum: ID, Created, From, To, NextTry, FileName, (unknown), Tries.
    public static List<QueueRow> QueueRows()
    {
        string raw = (string)((dynamic)Connect()).Status.UndeliveredMessages;
        var rows = new List<QueueRow>();
        foreach (var line in raw.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            var c = line.Split('\t');
            string At(int i) => i < c.Length ? c[i] : "";
            rows.Add(new QueueRow(int.TryParse(At(0), out var id) ? id : 0, At(1), At(2), At(3), At(4), At(5), int.TryParse(At(7), out var r) ? r : 0));
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
                int n = (int)st.SessionCount(1) + (int)st.SessionCount(3) + (int)st.SessionCount(5); // eSessionType: SMTP=1, POP3=3, IMAP=5
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

record LogLine(string Type, string Time, string? Ip, string Message);

// hMailServer log lines are tab-separated, every field in double quotes:
//   "SMTPD"|"POP3D"|"IMAPD"|"SMTPC"  thread  session  time  ip  message
//   "DEBUG"|"APPLICATION"|"TCPIP"|"ERROR"  thread  time  message
static class LogStore
{
    public static string Dir(IConfiguration c) => c["Logs:Directory"] ?? @"C:\Program Files (x86)\hMailServer\Logs";

    static readonly Regex NameRx = new(@"^(ERROR_)?hmailserver_\d{4}-\d{2}-\d{2}\.log$");
    public static bool NameOk(string n) => NameRx.IsMatch(n);   // also blocks path traversal

    public static readonly Regex CodeRx = new(@"Code: (HM\d+)");
    public static readonly Regex SevRx = new(@"Severity: \d \((\w+)\)");
    public static readonly Regex IpRx = new(@"Remote IP: ([0-9a-fA-F.:]+)");

    public static bool IsFailedLogin(LogLine l) =>
        l.Type is "SMTPD" or "POP3D" or "IMAPD" && l.Message.StartsWith("SENT:") &&
        (l.Message.Contains("Invalid user name or password") || l.Message.Contains("Authentication failed"));

    public static LogLine? ParseLine(string line)
    {
        var p = line.Split('\t');
        if (p.Length < 4) return null;
        for (int i = 0; i < p.Length; i++) p[i] = p[i].Trim('"');
        if (p[0] is "SMTPD" or "POP3D" or "IMAPD" or "SMTPC" && p.Length >= 6)
            return new LogLine(p[0], p[3], p[4], string.Join(' ', p.Skip(5)));
        return new LogLine(p[0], p[2], null, string.Join(' ', p.Skip(3)));
    }

    public static IEnumerable<LogLine> Read(string path)
    {
        // hMailServer keeps writing to today's file, so open it with shared access.
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs, Encoding.UTF8);
        string? line;
        while ((line = sr.ReadLine()) is not null)
        {
            var l = ParseLine(line);
            if (l is not null) yield return l;
        }
    }
}

record CritDto(string Field, string Header, string Match, string Value);
record ActDto(string Type, string To, string Folder, string Header, string Value);
record RuleDto(int Id, string Name, bool Active, bool MatchAll, bool Supported, List<CritDto> Criteria, List<ActDto> Actions);
record RuleInput(string Name, bool Active, bool MatchAll, List<CritDto> Criteria, List<ActDto> Actions);
record SettingDef(string Group, string Key, string Label, string Type);
record SettingRow(string Group, string Key, string Label, string Type, object? Value, string? Error);
record BlacklistRequest(string Ip);
record BlResult(string Zone, string Status, string Detail);

// Enumerations from hMailServer.idl: eRulePredefinedField, eRuleMatchType, eRuleActionType.
static class RuleMap
{
    public static readonly Dictionary<string, int> Fields = new()
        { ["from"] = 1, ["to"] = 2, ["cc"] = 3, ["subject"] = 4, ["body"] = 5, ["size"] = 6, ["recipients"] = 7, ["attempts"] = 8 };
    public static readonly Dictionary<string, int> Matches = new()
        { ["equals"] = 1, ["contains"] = 2, ["lt"] = 3, ["gt"] = 4, ["regex"] = 5, ["notcontains"] = 6, ["notequals"] = 7, ["wildcard"] = 8 };
    // Only the actions this UI can edit. Reply (3), run script (5), send using route (8) and bind to address (10)
    // are left alone: a rule that uses them is listed as read-only so a save cannot silently drop them.
    public static readonly Dictionary<string, int> Actions = new()
        { ["delete"] = 1, ["forward"] = 2, ["move"] = 4, ["stop"] = 6, ["setheader"] = 7, ["copy"] = 9 };

    public static string? Name(Dictionary<string, int> map, int value) => map.Where(x => x.Value == value).Select(x => x.Key).FirstOrDefault();

    public static string? Check(RuleInput i)
    {
        if (string.IsNullOrWhiteSpace(i.Name)) return "Tên rule không được để trống.";
        if (i.Criteria.Count == 0) return "Rule cần ít nhất một điều kiện: rule không có điều kiện sẽ không chạy hành động nào.";
        if (i.Actions.Count == 0) return "Rule cần ít nhất một hành động.";
        foreach (var c in i.Criteria)
        {
            bool custom = c.Field == "header";
            if (!custom && !Fields.ContainsKey(c.Field)) return "Trường điều kiện không hợp lệ.";
            if (custom && string.IsNullOrWhiteSpace(c.Header)) return "Điều kiện header tuỳ chỉnh cần tên header.";
            if (!Matches.ContainsKey(c.Match)) return "Kiểu so khớp không hợp lệ.";
        }
        foreach (var a in i.Actions)
        {
            if (!Actions.ContainsKey(a.Type)) return "Hành động không hợp lệ.";
            if (a.Type == "forward" && !HMail.ValidEmail(a.To)) return "Địa chỉ chuyển tiếp không hợp lệ.";
            if (a.Type == "move" && string.IsNullOrWhiteSpace(a.Folder)) return "Hãy nhập tên thư mục IMAP.";
            if (a.Type == "setheader" && string.IsNullOrWhiteSpace(a.Header)) return "Hãy nhập tên header.";
        }
        return null;
    }

    public static void Apply(dynamic rule, RuleInput i)
    {
        rule.Name = i.Name; rule.Active = i.Active; rule.UseAND = i.MatchAll; rule.Save();   // saved first: children need its ID
        for (int k = (int)rule.Criterias.Count - 1; k >= 0; k--) rule.Criterias.Item[k].Delete();
        for (int k = (int)rule.Actions.Count - 1; k >= 0; k--) rule.Actions.Item[k].Delete();
        foreach (var c in i.Criteria)
        {
            dynamic x = rule.Criterias.Add();
            if (c.Field == "header") { x.UsePredefined = false; x.HeaderField = c.Header; }
            else { x.UsePredefined = true; x.PredefinedField = Fields[c.Field]; }
            x.MatchType = Matches[c.Match]; x.MatchValue = c.Value; x.Save();
        }
        foreach (var a in i.Actions)
        {
            dynamic x = rule.Actions.Add();
            x.Type = Actions[a.Type];
            if (a.Type == "forward") x.To = a.To;
            if (a.Type == "move") x.IMAPFolder = a.Folder;
            if (a.Type == "setheader") { x.HeaderName = a.Header; x.Value = a.Value; }
            x.Save();
        }
    }
}

// Settings reached by property path from Application.Settings, read and written with late-bound InvokeMember.
// HostName, MaxMessageSize and RuleLoopLimit are confirmed in the COM documentation; the rest follow the
// hMailServer object model from memory, so a wrong name shows up as an error on that one row only.
static class SettingsMap
{
    public static readonly SettingDef[] All =
    {
        new("Chung", "HostName", "Tên máy chủ (host name)", "string"),
        new("Chung", "MaxMessageSize", "Kích thước thư tối đa (KB)", "int"),
        new("Chung", "RuleLoopLimit", "Giới hạn vòng lặp rule", "int"),
        new("Giao thức", "ServiceSMTP", "Bật dịch vụ SMTP", "bool"),
        new("Giao thức", "ServicePOP3", "Bật dịch vụ POP3", "bool"),
        new("Giao thức", "ServiceIMAP", "Bật dịch vụ IMAP", "bool"),
        new("Giao thức", "SMTPRelayer", "Máy chủ relay cố định (smart host)", "string"),
        new("Giao thức", "SMTPRelayerPort", "Cổng relay cố định", "int"),
        new("Giao thức", "SMTPDeliveryBindToIP", "Địa chỉ IP dùng khi gửi thư đi", "string"),
        new("SMTP", "WelcomeSMTP", "Lời chào SMTP", "string"),
        new("SMTP", "SMTPNoOfTries", "Số lần thử gửi", "int"),
        new("SMTP", "SMTPMinutesBetweenTry", "Phút giữa các lần thử", "int"),
        new("SMTP", "MaxNumberOfMXHosts", "Số MX host tối đa", "int"),
        new("SMTP", "MaxSMTPRecipientsInBatch", "Số người nhận tối đa mỗi lô", "int"),
        new("POP3 / IMAP", "WelcomePOP3", "Lời chào POP3", "string"),
        new("POP3 / IMAP", "WelcomeIMAP", "Lời chào IMAP", "string"),
        new("Chống thư rác", "AntiSpam.SpamMarkThreshold", "Ngưỡng đánh dấu thư rác", "int"),
        new("Chống thư rác", "AntiSpam.SpamDeleteThreshold", "Ngưỡng xoá thư rác", "int"),
        new("Chống thư rác", "AntiSpam.PrependSubject", "Thêm tiền tố vào tiêu đề thư rác", "bool"),
        new("Chống thư rác", "AntiSpam.PrependSubjectText", "Nội dung tiền tố", "string"),
        new("Chống virus", "AntiVirus.ClamAVEnabled", "Bật ClamAV", "bool"),
        new("Chống virus", "AntiVirus.ClamAVHost", "Máy chủ ClamAV", "string"),
        new("Chống virus", "AntiVirus.ClamAVPort", "Cổng ClamAV", "int"),
        new("Nhật ký", "Logging.Enabled", "Bật ghi log", "bool"),
        new("Nhật ký", "Logging.LogApplication", "Log ứng dụng", "bool"),
        new("Nhật ký", "Logging.LogSMTP", "Log SMTP", "bool"),
        new("Nhật ký", "Logging.LogPOP3", "Log POP3", "bool"),
        new("Nhật ký", "Logging.LogIMAP", "Log IMAP", "bool"),
        new("Nhật ký", "Logging.LogTCPIP", "Log TCP/IP", "bool"),
        new("Nhật ký", "Logging.LogDebug", "Log debug", "bool"),
    };

    public static (object Holder, string Prop) Resolve(object root, string path)
    {
        var parts = path.Split('.');
        object cur = root;
        for (int i = 0; i < parts.Length - 1; i++) cur = Get(cur, parts[i])!;
        return (cur, parts[^1]);
    }
    public static object? Get(object o, string name) => o.GetType().InvokeMember(name, BindingFlags.GetProperty, null, o, null);
    public static void Set(object o, string name, object v) => o.GetType().InvokeMember(name, BindingFlags.SetProperty, null, o, new[] { v });
    public static string Short(Exception e) => (e is TargetInvocationException { InnerException: { } inner } ? inner : e).Message;
}

static class Dnsbl
{
    public static readonly string[] Zones =
    {
        "zen.spamhaus.org", "bl.spamcop.net", "b.barracudacentral.org", "psbl.surriel.com",
        "dnsbl-1.uceprotect.net", "ix.dnsbl.manitu.net", "dnsbl.dronebl.org",
    };

    public static async Task<BlResult> CheckAsync(string reversedIp, string zone)
    {
        try
        {
            using var cts = new CancellationTokenSource(4000);
            var found = await Dns.GetHostAddressesAsync($"{reversedIp}.{zone}", cts.Token);
            var code = found.FirstOrDefault()?.ToString() ?? "";
            // 127.255.255.x is the answer DNSBLs give to resolvers they refuse to serve (for example public DNS), not a listing.
            if (code.StartsWith("127.255.255.")) return new BlResult(zone, "error", $"DNSBL từ chối truy vấn ({code}): thường do dùng DNS công cộng hoặc vượt hạn mức.");
            return new BlResult(zone, "listed", code);
        }
        catch (SocketException e) when (e.SocketErrorCode is SocketError.HostNotFound or SocketError.NoData) { return new BlResult(zone, "clean", ""); }
        catch (OperationCanceledException) { return new BlResult(zone, "error", "Hết thời gian chờ DNS."); }
        catch (Exception e) { return new BlResult(zone, "error", e.Message); }
    }
}

record LiveResp(string File, long Offset, List<LogLine> Lines, string? Notice);
record DnsblDto(int Id, string Host, bool Active, int Score, string ExpectedResult, string RejectMessage);
record DnsblInput(string Host, bool Active, int Score, string ExpectedResult, string RejectMessage);
record QueueRow(int Id, string Created, string From, string To, string NextTry, string FileName, int Retries);
record ResField(string Json, string Com, string Type, bool WriteOnly = false);
record ResDef(string Name, string Path, ResField[] Fields, Func<Dictionary<string, JsonElement>, string?>? Check = null);
record PortDto(int Id, int Protocol, string Address, int Port, int Security, int CertificateId, bool Listening);
record PortInput(int Protocol, string Address, int Port, int Security, int CertificateId);
record DiagRequest(string Domain, string? ServerIp, string? Selector);
record DiagCheck(string Id, string Title, string Status, string Detail);
record CertInfo(string? Subject, string? Issuer, string? NotAfter, int? DaysLeft, List<string> Names, bool? HostMatch, bool? KeyOk, string? Problem);
record CertRow(int Id, string Name, string CertificateFile, string PrivateKeyFile, CertInfo Info);
record CertInput(string Name, string CertificateFile, string PrivateKeyFile);

static class Diag
{
    static readonly LookupClient Lookup = new(new LookupClientOptions { Timeout = TimeSpan.FromSeconds(4), Retries = 1, UseCache = false });

    static async Task<List<string>> TxtAsync(string name)
    {
        try { return (await Lookup.QueryAsync(name, QueryType.TXT)).Answers.TxtRecords().Select(t => string.Concat(t.Text)).ToList(); }
        catch { return new List<string>(); }
    }

    static async Task<string> BannerAsync(string host)
    {
        using var cts = new CancellationTokenSource(5000);
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(host, 25, cts.Token);
        var buf = new byte[256];
        int n = await tcp.GetStream().ReadAsync(buf, cts.Token);
        return Encoding.ASCII.GetString(buf, 0, n).Trim();
    }

    public static async Task<List<DiagCheck>> RunAsync(string domain, bool active, string? serverIp, string? selector, string host)
    {
        var checks = new List<DiagCheck>();
        void Add(string id, string title, string status, string detail) => checks.Add(new DiagCheck(id, title, status, detail));
        IPAddress.TryParse(serverIp, out var srv);

        Add("local", "Tên miền trong hMailServer", active ? "ok" : "warn", active ? "Tên miền tồn tại và đang hoạt động." : "Tên miền tồn tại nhưng đang bị tắt, nên không nhận thư.");

        // MX
        var mxs = new List<(int Pref, string Host)>();
        try { mxs = (await Lookup.QueryAsync(domain, QueryType.MX)).Answers.MxRecords().OrderBy(m => m.Preference).Select(m => ((int)m.Preference, m.Exchange.Value.TrimEnd('.'))).ToList(); }
        catch (Exception e) { Add("mx", "Bản ghi MX", "fail", $"Không truy vấn được DNS: {e.Message}"); }
        if (mxs.Count > 0) Add("mx", "Bản ghi MX", "ok", string.Join("; ", mxs.Select(m => $"{m.Pref} {m.Host}")));
        else if (!checks.Any(c => c.Id == "mx")) Add("mx", "Bản ghi MX", "fail", "Chưa có bản ghi MX, thư từ bên ngoài sẽ không tới được tên miền này.");

        var mxIps = new List<IPAddress>();
        foreach (var m in mxs.Take(5))
        {
            try
            {
                var ips = (await Lookup.QueryAsync(m.Host, QueryType.A)).Answers.ARecords().Select(a => a.Address).ToList();
                mxIps.AddRange(ips);
                Add("mx-a:" + m.Host, $"A của {m.Host}", ips.Count > 0 ? "ok" : "fail", ips.Count > 0 ? string.Join(", ", ips) : "MX trỏ tới tên không có bản ghi A.");
            }
            catch (Exception e) { Add("mx-a:" + m.Host, $"A của {m.Host}", "fail", e.Message); }
        }
        if (srv is not null && mxs.Count > 0)
            Add("mx-self", "MX trỏ về máy chủ này", mxIps.Contains(srv) ? "ok" : "fail", mxIps.Contains(srv) ? $"Có MX trỏ tới {srv}." : $"Không MX nào trỏ tới {srv}.");
        else Add("mx-self", "MX trỏ về máy chủ này", "info", "Nhập IP công khai của máy chủ để kiểm tra mục này, PTR và host name.");

        // Host name and reverse DNS
        if (srv is not null)
        {
            if (!string.IsNullOrWhiteSpace(host))
            {
                try
                {
                    var hostIps = (await Lookup.QueryAsync(host, QueryType.A)).Answers.ARecords().Select(a => a.Address).ToList();
                    Add("host-a", $"A của host name ({host})", hostIps.Contains(srv) ? "ok" : "warn", hostIps.Count == 0 ? "Host name chưa có bản ghi A." : $"Trỏ tới {string.Join(", ", hostIps)}.");
                }
                catch (Exception e) { Add("host-a", $"A của host name ({host})", "fail", e.Message); }
            }
            try
            {
                var ptr = (await Lookup.QueryReverseAsync(srv)).Answers.PtrRecords().Select(p => p.PtrDomainName.Value.TrimEnd('.')).ToList();
                bool match = ptr.Any(n => string.Equals(n, host, StringComparison.OrdinalIgnoreCase));
                Add("ptr", "Reverse DNS (PTR)", ptr.Count == 0 ? "fail" : match ? "ok" : "warn",
                    ptr.Count == 0 ? $"{srv} chưa có PTR; nhiều máy chủ nhận thư từ chối thư gửi từ IP không có PTR." : $"{string.Join(", ", ptr)}{(match ? "" : $" (khác host name {host})")}");
            }
            catch (Exception e) { Add("ptr", "Reverse DNS (PTR)", "fail", e.Message); }
        }

        // SPF
        var spf = (await TxtAsync(domain)).Where(t => t.StartsWith("v=spf1", StringComparison.OrdinalIgnoreCase)).ToList();
        if (spf.Count == 0) Add("spf", "SPF", "warn", "Chưa có bản ghi TXT bắt đầu bằng v=spf1.");
        else if (spf.Count > 1) Add("spf", "SPF", "fail", "Có nhiều hơn một bản ghi SPF; chuẩn chỉ cho phép một, nên kết quả sẽ không hợp lệ.");
        else Add("spf", "SPF", spf[0].Contains("-all") || spf[0].Contains("~all") ? "ok" : "warn",
                 spf[0] + (spf[0].Contains("-all") || spf[0].Contains("~all") ? "" : "  (nên kết thúc bằng -all hoặc ~all)"));

        // DMARC
        var dmarc = (await TxtAsync("_dmarc." + domain)).Where(t => t.StartsWith("v=DMARC1", StringComparison.OrdinalIgnoreCase)).ToList();
        if (dmarc.Count == 0) Add("dmarc", "DMARC", "warn", $"Chưa có bản ghi TXT tại _dmarc.{domain}.");
        else
        {
            var policy = System.Text.RegularExpressions.Regex.Match(dmarc[0], @"\bp=(\w+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Groups[1].Value.ToLowerInvariant();
            Add("dmarc", "DMARC", policy == "none" ? "warn" : "ok", dmarc[0] + (policy == "none" ? "  (p=none chỉ giám sát, chưa chặn thư giả mạo)" : ""));
        }

        // DKIM
        if (string.IsNullOrWhiteSpace(selector)) Add("dkim", "DKIM", "info", "Nhập selector DKIM để kiểm tra bản ghi khoá công khai.");
        else
        {
            var dk = (await TxtAsync($"{selector}._domainkey.{domain}")).FirstOrDefault(t => t.Contains("p="));
            Add("dkim", $"DKIM ({selector})", dk is null ? "fail" : "ok", dk is null ? $"Không có bản ghi khoá công khai tại {selector}._domainkey.{domain}." : dk.Length > 120 ? dk[..120] + "…" : dk);
        }

        // Port 25 on the MX hosts, tested from this machine
        foreach (var m in mxs.Take(3))
        {
            try
            {
                var banner = await BannerAsync(m.Host);
                Add("smtp:" + m.Host, $"Cổng 25 của {m.Host}", banner.StartsWith("220") ? "ok" : "warn", banner.Length > 120 ? banner[..120] : banner);
            }
            catch (Exception e) { Add("smtp:" + m.Host, $"Cổng 25 của {m.Host}", "fail", e is OperationCanceledException ? "Hết thời gian chờ kết nối." : e.Message); }
        }
        return checks;
    }
}

static class CertCheck
{
    public static CertInfo Analyze(string certFile, string keyFile, string host)
    {
        if (string.IsNullOrWhiteSpace(certFile) || !File.Exists(certFile))
            return new CertInfo(null, null, null, null, new List<string>(), null, null, "Không tìm thấy file chứng chỉ trên máy chủ.");
        try
        {
            using var cert = new X509Certificate2(certFile);
            var names = SanNames(cert);
            if (names.Count == 0 && cert.GetNameInfo(X509NameType.SimpleName, false) is { Length: > 0 } cn) names.Add(cn);
            int days = (int)Math.Floor((cert.NotAfter - DateTime.Now).TotalDays);
            bool? keyOk = null; string? problem = null;
            if (string.IsNullOrWhiteSpace(keyFile) || !File.Exists(keyFile)) problem = "Không tìm thấy file khoá riêng trên máy chủ.";
            else
            {
                try { using var pair = X509Certificate2.CreateFromPemFile(certFile, keyFile); keyOk = true; }
                catch (Exception e) { problem = $"Không xác nhận được khoá riêng khớp chứng chỉ ({e.Message}). Có thể khoá có passphrase hoặc không ở dạng PEM."; }
            }
            return new CertInfo(cert.Subject, cert.Issuer, cert.NotAfter.ToString("yyyy-MM-dd"), days, names, HostMatches(host, names), keyOk, problem);
        }
        catch (Exception e) { return new CertInfo(null, null, null, null, new List<string>(), null, null, $"Không đọc được chứng chỉ: {e.Message}"); }
    }

    // Subject Alternative Name (2.5.29.17): GeneralNames, dNSName is context tag [2].
    static List<string> SanNames(X509Certificate2 cert)
    {
        var names = new List<string>();
        try
        {
            var ext = cert.Extensions["2.5.29.17"];
            if (ext is null) return names;
            var seq = new AsnReader(ext.RawData, AsnEncodingRules.DER).ReadSequence();
            var dns = new Asn1Tag(TagClass.ContextSpecific, 2);
            while (seq.HasData)
            {
                if (seq.PeekTag() == dns) names.Add(seq.ReadCharacterString(UniversalTagNumber.IA5String, dns));
                else seq.ReadEncodedValue();
            }
        }
        catch { /* keep what was read */ }
        return names;
    }

    static bool? HostMatches(string host, List<string> names)
    {
        if (string.IsNullOrWhiteSpace(host) || names.Count == 0) return null;
        return names.Any(n => string.Equals(n, host, StringComparison.OrdinalIgnoreCase) ||
            (n.StartsWith("*.") && host.Contains('.') && string.Equals(n[2..], host[(host.IndexOf('.') + 1)..], StringComparison.OrdinalIgnoreCase)));
    }
}

// Collections under Application.Settings that share one shape: items with ID, properties, Save() and Delete().
// Fields are whitelisted per resource; routes, relays and the white list follow the object model from memory
// (route properties match the coax webadmin source), so a wrong name surfaces as an error on that resource only.
static class Resources
{
    static string S(Dictionary<string, JsonElement> b, string k) => b.TryGetValue(k, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "").Trim() : "";
    static int N(Dictionary<string, JsonElement> b, string k, int d = 0) => b.TryGetValue(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : d;
    static bool HostOk(string h) => HMail.ValidDomain(h.ToLowerInvariant()) || IPAddress.TryParse(h, out _);

    public static readonly ResDef[] All =
    {
        new("routes", "Routes", new ResField[]
        {
            new("domainName", "DomainName", "string"), new("description", "Description", "string"),
            new("targetHost", "TargetSMTPHost", "string"), new("targetPort", "TargetSMTPPort", "int"),
            new("tries", "NumberOfTries", "int"), new("minutesBetween", "MinutesBetweenTry", "int"),
            new("senderLocal", "TreatSenderAsLocalDomain", "bool"), new("recipientLocal", "TreatRecipientAsLocalDomain", "bool"),
            new("requiresAuth", "RelayerRequiresAuth", "bool"), new("authUser", "RelayerAuthUsername", "string"),
            new("authPassword", "RelayerAuthPassword", "string", true), new("security", "ConnectionSecurity", "int"),
        }, b =>
        {
            var d = S(b, "domainName");
            if (d.StartsWith("*.")) d = d[2..];
            if (!HMail.ValidDomain(d.ToLowerInvariant())) return "Tên miền route không hợp lệ (có thể dùng *.example.com).";
            var hosts = S(b, "targetHost").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (hosts.Length == 0 || hosts.Any(h => !HostOk(h))) return "Máy chủ đích không hợp lệ (tên máy chủ hoặc IP, cách nhau bằng dấu phẩy).";
            if (N(b, "targetPort", 25) is < 1 or > 65535) return "Cổng phải từ 1 đến 65535.";
            if (N(b, "security") is < 0 or > 3) return "Kiểu bảo mật không hợp lệ.";
            return null;
        }),
        new("relays", "IncomingRelays", new ResField[]
        {
            new("name", "Name", "string"), new("lowerIp", "LowerIP", "string"), new("upperIp", "UpperIP", "string"),
        }, b =>
            string.IsNullOrWhiteSpace(S(b, "name")) ? "Tên không được để trống."
            : !IPAddress.TryParse(S(b, "lowerIp"), out _) || !IPAddress.TryParse(S(b, "upperIp"), out _) ? "Địa chỉ IP không hợp lệ." : null),
        new("surbl", "AntiSpam.SURBLServers", new ResField[]
        {
            new("dnsHost", "DNSHost", "string"), new("score", "Score", "int"), new("rejectMessage", "RejectMessage", "string"), new("active", "Active", "bool"),
        }, b => !HMail.ValidDomain(S(b, "dnsHost").ToLowerInvariant()) ? "Máy chủ SURBL không hợp lệ (vd. multi.surbl.org)."
            : N(b, "score") is < 0 or > 100 ? "Điểm phải từ 0 đến 100." : null),
        new("greywhite", "AntiSpam.GreyListingWhiteAddresses", new ResField[]
        {
            new("ipAddress", "IPAddress", "string"), new("description", "Description", "string"),
        }, b => string.IsNullOrWhiteSpace(S(b, "ipAddress")) || S(b, "ipAddress").Contains(' ') ? "Hãy nhập một địa chỉ IP (không có khoảng trắng)." : null),
    };

    static dynamic Collection(ResDef d)
    {
        object cur = HMail.Connect().Settings;
        foreach (var part in d.Path.Split('.')) cur = SettingsMap.Get(cur, part)!;
        return cur;
    }

    public static IResult List(ResDef d)
    {
        dynamic col = Collection(d);
        var rows = new List<Dictionary<string, object?>>();
        for (int i = 0; i < (int)col.Count; i++)
        {
            object it = col.Item[i];
            var row = new Dictionary<string, object?> { ["id"] = (int)((dynamic)it).ID };
            foreach (var f in d.Fields.Where(f => !f.WriteOnly))
            {
                try { row[f.Json] = SettingsMap.Get(it, f.Com); } catch { row[f.Json] = null; }
            }
            rows.Add(row);
        }
        return Results.Ok(rows);
    }

    public static IResult Save(ResDef d, int? id, Dictionary<string, JsonElement> body)
    {
        if (d.Check?.Invoke(body) is { } err) return HMail.Bad(err);
        dynamic col = Collection(d);
        object? item = id is null ? col.Add() : HMail.Find(col, id.Value);
        if (item is null) return Results.NotFound();
        foreach (var f in d.Fields)
        {
            if (!body.TryGetValue(f.Json, out var v)) continue;
            if (f.WriteOnly && (v.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(v.GetString()))) continue;   // blank password = keep
            object val;
            try { val = f.Type switch { "bool" => v.GetBoolean(), "int" => v.GetInt32(), _ => v.GetString() ?? "" }; }
            catch (Exception e) when (e is InvalidOperationException or FormatException) { return HMail.Bad($"Giá trị của {f.Json} không hợp lệ."); }
            SettingsMap.Set(item, f.Com, val);
        }
        ((dynamic)item).Save();
        return Results.NoContent();
    }

    public static IResult Remove(ResDef d, int id)
    {
        dynamic? item = HMail.Find(Collection(d), id);
        if (item is null) return Results.NotFound();
        item.Delete();
        return Results.NoContent();
    }
}
