using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using MySqlConnector;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ForceNames;

public class ForceNamesPlugin : BasePlugin
{
    public override string ModuleName => "ForceNames";
    public override string ModuleVersion => "1.2.0";
    public override string ModuleAuthor => "Midori";

    // Config
    private ForceNamesConfig _cfg = new();
    private readonly JsonSerializerOptions _jsonOpt = new(JsonSerializerDefaults.Web)
    { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    // Paths
    private static string Abs(string p) => Path.GetFullPath(p);
    private string DesiredDir  => Abs(Path.Combine(ModuleDirectory, "..", "..", "configs", "plugins", "ForceNames"));
    private string DesiredPath => Abs(Path.Combine(DesiredDir, "ForceNames.json"));

    // MySQL Schema
    private const string TBL = "forcenames_players";
    private const string COL_SID  = "steamid64";
    private const string COL_NICK = "nickname";

    // Lifecycle, Timer
    private bool _unloading;
    private int _timerGen = 0;

    public override void Load(bool hotReload)
    {
        _unloading = false;
        EnsureConfig();

        if (_cfg.UseMySql)
        {
            if (TryLoadMappingsFromMySql(out var map))
            {
                _cfg.Mappings = map;
                Console.WriteLine($"[ForceNames] MySQL loaded mappings: {_cfg.Mappings.Count}");
            }
            else
            {
                Console.WriteLine("[ForceNames] MySQL load failed. Using last in-memory mappings.");
            }
        }

        StartPeriodicApply();

        AddCommand("css_fn", "Force name by SteamID64", CmdForceName);
        AddCommand("css_unfn", "Remove forced name", CmdUnforceName);
        AddCommand("css_fn_reload", "Reload config (prefers subfolder)", CmdReload);
        AddCommand("css_fn_list", "List mappings", CmdList);
        AddCommand("css_fn_players", "List online players", CmdFnPlayer);
    }

    public override void Unload(bool hotReload) => _unloading = true;

    // Periodic Apply
    private void StartPeriodicApply()
    {
        _timerGen++;
        var myGen = _timerGen;
        var interval = Math.Clamp(_cfg.ApplyIntervalSec, 1.0f, 60.0f);

        AddTimer(interval, () => TimerTick(myGen));
    }

    private void TimerTick(int myGen)
    {
        if (_unloading || myGen != _timerGen) return;
        try { ApplyAllOnlinePlayers(); } catch (Exception ex) { Console.WriteLine($"[ForceNames] periodic: {ex}"); }

        var interval = Math.Clamp(_cfg.ApplyIntervalSec, 1.0f, 60.0f);
        if (myGen == _timerGen) AddTimer(interval, () => TimerTick(myGen));
    }

    private void ApplyAllOnlinePlayers()
    {
        foreach (var p in Utilities.GetPlayers())
            if (p?.IsValid == true) TryApplyForcedName(p);
    }

    // Players Snapshot
    private sealed record PRef(int Idx, string Sid, string Name);

    private List<PRef> SnapshotPlayers()
    {
        var list = Utilities.GetPlayers()
            .Where(p => p != null && p.IsValid && !p.IsBot)
            .Select(p => new { Sid = p!.SteamID.ToString(), Name = p.PlayerName ?? "(noname)" })
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase) // In alphabetical order
            .ToList();

        var ret = new List<PRef>(list.Count);
        for (int i = 0; i < list.Count; i++)
            ret.Add(new PRef(i + 1, list[i].Sid, list[i].Name));
        return ret;
    }

    private void ReplyLine(CCSPlayerController? caller, CommandInfo cmd, string msg)
    {
        if (caller != null && caller.IsValid) caller.PrintToChat(" " + msg);
        else cmd.ReplyToCommand(msg);
    }

    private bool TryResolveTargetSid(CCSPlayerController? caller, CommandInfo cmd, string token, out string sid, out string dispName)
    {
        sid = "";
        dispName = "";

        token = (token ?? "").Trim().Trim('"');
        if (token.Length == 0) return false;

        // 1) SteamID64
        if (token.Length >= 16 && token.All(char.IsDigit))
        {
            sid = token;
            dispName = token;
            return true;
        }

        var ps = SnapshotPlayers();
        if (ps.Count == 0)
        {
            { caller?.PrintToChat($"[{ChatColors.Green}ForceNames{ChatColors.White}] No players are connected."); return false; }
        }

        // 2) Number
        var t = token.StartsWith("#", StringComparison.Ordinal) ? token[1..] : token;
        if (int.TryParse(t, out var idx))
        {
            var hit = ps.FirstOrDefault(x => x.Idx == idx);
            if (hit != null)
            {
                sid = hit.Sid;
                dispName = hit.Name;
                return true;
            }
            { caller?.PrintToChat($"[{ChatColors.Green}ForceNames{ChatColors.White}] That number is out of range: {idx} (1~{ps.Count})"); return false; }
        }

        // 3) Name
        var exact = ps.Where(x => x.Name.Equals(token, StringComparison.OrdinalIgnoreCase)).ToList();
        var hits = exact.Count > 0
            ? exact
            : ps.Where(x => x.Name.Contains(token, StringComparison.OrdinalIgnoreCase)).ToList();

        if (hits.Count == 1)
        {
            sid = hits[0].Sid;
            dispName = hits[0].Name;
            return true;
        }

        if (hits.Count > 1)
        {
            ReplyLine(caller, cmd, $"[{ChatColors.Green}ForceNames{ChatColors.White}] '{token}' is ambiguous. Please specify by number (e.g., !fn 2 Midori).");
            foreach (var h in hits.Take(8)) ReplyLine(caller, cmd, $"[{h.Idx}] {h.Name}");
            if (hits.Count > 8) ReplyLine(caller, cmd, $"... +{hits.Count - 8} more");
            return false;
        }

        { caller?.PrintToChat($"[{ChatColors.Green}ForceNames{ChatColors.White}] Could not find '{token}'. Use !fn_players to see the player list."); return false; }
    }

    // Clean Nickname
    private static string CleanNickname(string s)
    {
        s = (s ?? string.Empty).Trim();
        s = string.Concat(s.Where(c => !char.IsControl(c)));
        if (s.Length > 31) s = s[..31];
        return s;
    }

    [GameEventHandler]
    public HookResult OnPlayerFullConnect(EventPlayerConnectFull e, GameEventInfo info)
    {
        var p = e.Userid;
        if (p?.IsValid == true) TryApplyForcedName(p);
        return HookResult.Continue;
    }

    private void TryApplyForcedName(CCSPlayerController player)
    {
        try
        {
            if (player == null || !player.IsValid || player.IsBot) return;

            var sid = player.SteamID.ToString();
            if (_cfg.Mappings != null && _cfg.Mappings.TryGetValue(sid, out var forced))
            {
                var current = player.PlayerName ?? string.Empty;
                if (string.Equals(current, forced, StringComparison.Ordinal)) return;

                player.PlayerName = forced;
                try { Utilities.SetStateChanged(player, "CCSPlayerController", "m_iszPlayerName"); } catch { }

                Console.WriteLine($"[ForceNames] Applied '{forced}' to {sid} (was '{current}')");
            }
        }
        catch (Exception ex) { Console.WriteLine($"[ForceNames] TryApplyForcedName error: {ex}"); }
    }

    // Commands
    [ConsoleCommand("css_fn")]
    private void CmdForceName(CCSPlayerController? caller, CommandInfo cmd)
    {
        if (caller != null && !AdminManager.PlayerHasPermissions(caller, "@css/root"))
        { caller?.PrintToChat($" {ChatColors.Red}You do not have permission."); return; }

        if (cmd.ArgCount < 3) 
        { caller?.PrintToChat($"[{ChatColors.Green}ForceNames{ChatColors.White}] {ChatColors.Yellow}Usage: !fn <steamid64|#idx|name> <nickname...>{ChatColors.White} (list: {ChatColors.Yellow}!fn_players{ChatColors.White})"); return; }

        var token = cmd.GetArg(1);
        if (!TryResolveTargetSid(caller, cmd, token, out var sid, out var who))
            return;
        
        var parts = new List<string>();
        for (int i = 2; i < cmd.ArgCount; i++) parts.Add(cmd.GetArg(i));
        var rawNickname = string.Join(' ', parts);
        var nickname = CleanNickname(rawNickname);

        bool ok;
        if (_cfg.UseMySql)
        {
            ok = UpsertMySqlMapping(sid, nickname);
            if (ok) _cfg.Mappings[sid] = nickname;
        }
        else 
        { 
            _cfg.Mappings[sid] = nickname; 
            SaveConfig(); 
            ok = true; 
        }

        ReplyLine(caller, cmd, ok
            ? $"[{ChatColors.Green}ForceNames{ChatColors.White}] {ChatColors.Green}SUCCESS!{ChatColors.White}: {who} ({sid}) => '{nickname}'"
            : $"[{ChatColors.Green}ForceNames{ChatColors.White}] {ChatColors.Red}FAIL{ChatColors.White}: DB write failed");

        if (ok && ulong.TryParse(sid, out var sid64))
        {
            var target = Utilities.GetPlayerFromSteamId(sid64);
            if (target?.IsValid == true) TryApplyForcedName(target);
        }
    }

    [ConsoleCommand("css_unfn")]
    private void CmdUnforceName(CCSPlayerController? caller, CommandInfo cmd)
    {
        if (caller != null && !AdminManager.PlayerHasPermissions(caller, "@css/root"))
        { caller?.PrintToChat($" {ChatColors.Red}You do not have permission."); return; }

        if (cmd.ArgCount < 2) { caller?.PrintToChat($"[{ChatColors.Green}ForceNames{ChatColors.White}] {ChatColors.Yellow}Usage: !unfn <steamid64|#idx|name>{ChatColors.White} (list: {ChatColors.Yellow}!fn_players{ChatColors.White})"); return; }
        
        var token = cmd.GetArg(1);
        if (!TryResolveTargetSid(caller, cmd, token, out var sid, out var who))
            return;

        bool ok;

        if (_cfg.UseMySql)
        {
            ok = DeleteMySqlMapping(sid);
            if (ok) _cfg.Mappings.Remove(sid);
        }
        else
        {
            ok = _cfg.Mappings.Remove(sid);
            if (ok) SaveConfig();
        }

        if (ok)
            caller?.PrintToChat($"[{ChatColors.Green}ForceNames{ChatColors.White}] {ChatColors.Red}Unset: {ChatColors.Purple}{who}{ChatColors.White} ({sid})");
        else
            caller?.PrintToChat($"[{ChatColors.Green}ForceNames{ChatColors.White}] No mapping for: {ChatColors.Purple}{who}{ChatColors.White} ({sid})");
    }

    [ConsoleCommand("css_fn_reload")]
    private void CmdReload(CCSPlayerController? caller, CommandInfo cmd)
    {
        if (caller != null && !AdminManager.PlayerHasPermissions(caller, "@css/root"))
        { caller?.PrintToChat($" {ChatColors.Red}You do not have permission."); return; }

        ReloadConfigAndApply(cmd);
    }

    [ConsoleCommand("css_fn_list")]
    private void CmdList(CCSPlayerController? caller, CommandInfo cmd)
    {
        if (caller != null && !AdminManager.PlayerHasPermissions(caller, "@css/root"))
        { caller?.PrintToChat($" {ChatColors.Red}You do not have permission."); return; }
        if (_cfg.Mappings.Count == 0) { caller?.PrintToChat($"[{ChatColors.Green}ForceNames{ChatColors.White}] empty"); return; }
        foreach (var kv in _cfg.Mappings) 
            caller?.PrintToChat($"{kv.Key} => {ChatColors.Purple}{kv.Value}{ChatColors.White}");
    }

    [ConsoleCommand("css_fn_players")]
    private void CmdFnPlayer(CCSPlayerController? caller, CommandInfo cmd)
    {
        if (caller != null && !AdminManager.PlayerHasPermissions(caller, "@css/root"))
        { caller?.PrintToChat($" {ChatColors.Red}You do not have permission."); return; }

        var ps = SnapshotPlayers();
        if (ps.Count == 0) { ReplyLine(caller, cmd, "[ForceNames] No players are connected."); return; }

        ReplyLine(caller, cmd, $"[{ChatColors.Green}ForceNames{ChatColors.White}] Online players:");
        foreach (var p in ps)
            ReplyLine(caller, cmd, $"[{p.Idx}] {ChatColors.Purple}{p.Name} - {p.Sid}");
    }

    // Reload Core (JSON, MySQL)
    private void ReloadConfigAndApply(CommandInfo cmd)
    {
        if (!_cfg.UseMySql)
        {
            if (!File.Exists(DesiredPath)) { SaveConfig(); cmd.ReplyToCommand($"[{ChatColors.Green}ForceNames{ChatColors.White}] Created new config."); return; }
            try
            {
                var json = File.ReadAllText(DesiredPath);
                _cfg = JsonSerializer.Deserialize<ForceNamesConfig>(json, _jsonOpt) ?? _cfg;
                SaveConfig();
                ApplyAllOnlinePlayers();
                StartPeriodicApply();
                cmd.CallingPlayer?.PrintToChat($"[{ChatColors.Green}ForceNames{ChatColors.White}] Reloaded JSON & applied.");
            }
            catch (Exception ex) { cmd.ReplyToCommand($"[{ChatColors.Green}ForceNames{ChatColors.White}] Reload failed: {ex.Message}"); }
            return;
        }

        // MySQL Mode
        if (TryLoadMappingsFromMySql(out var map))
        {
            _cfg.Mappings = map;
            ApplyAllOnlinePlayers();
            StartPeriodicApply();
            cmd.ReplyToCommand($"[{ChatColors.Green}ForceNames{ChatColors.White}] Reloaded MySQL & applied. ({map.Count} rows)");
        }
        else cmd.ReplyToCommand($"[{ChatColors.Green}ForceNames{ChatColors.White}] MySQL reload failed. See console.");
    }

    // Config
    private void EnsureConfig()
    {
        try
        {
            if (File.Exists(DesiredPath)) { LoadConfig(); return; }
            Directory.CreateDirectory(DesiredDir);
            _cfg = new ForceNamesConfig();
            SaveConfig();
            Console.WriteLine($"[ForceNames] Created default: {DesiredPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ForceNames] EnsureConfig error: {ex}");
            _cfg = new ForceNamesConfig();
        }
    }

    private void LoadConfig()
    {
        var json = File.ReadAllText(DesiredPath);
        _cfg = JsonSerializer.Deserialize<ForceNamesConfig>(json, _jsonOpt) ?? new ForceNamesConfig();
    }

    private void SaveConfig()
    {
        Directory.CreateDirectory(DesiredDir);
        var json = JsonSerializer.Serialize(_cfg, _jsonOpt);
        var tmp = DesiredPath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, DesiredPath, true);
    }

    // MySQL
    private MySqlConnection OpenMySqlConnectionEnsuringDatabase()
    {
        var csb = new MySqlConnectionStringBuilder {
            Server = _cfg.DatabaseHost,
            Port = (uint)_cfg.DatabasePort,
            UserID = _cfg.DatabaseUser,
            Password = _cfg.DatabasePassword,
            Database = _cfg.DatabaseName,
            SslMode = MySqlSslMode.None
        };

        try
        {
            var conn = new MySqlConnection(csb.ConnectionString);
            conn.Open();
            return conn;
        }
        catch (MySqlException ex) when (ex.Number == 1049)
        {
            var csbNoDb = new MySqlConnectionStringBuilder(csb.ConnectionString) { Database = "" };
            using var serverConn = new MySqlConnection(csbNoDb.ConnectionString);
            serverConn.Open();
            using (var cmd = new MySqlCommand(
                $"CREATE DATABASE IF NOT EXISTS `{_cfg.DatabaseName}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci", serverConn))
            {
                cmd.ExecuteNonQuery();
            }
            serverConn.Close();

            var conn2 = new MySqlConnection(csb.ConnectionString);
            conn2.Open();
            return conn2;
        }
    }

    private void EnsureMySqlTable(MySqlConnection conn)
    {
        var sql = $@"
    CREATE TABLE IF NOT EXISTS `{TBL}` (
    `{COL_SID}`  VARCHAR(20)  NOT NULL,
    `{COL_NICK}` VARCHAR(64)  NOT NULL,
    PRIMARY KEY (`{COL_SID}`),
    KEY `idx_{TBL}_{COL_NICK}` (`{COL_NICK}`)
    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;";
        using var cmd = new MySqlCommand(sql, conn);
        cmd.ExecuteNonQuery();
    }

    private bool TryLoadMappingsFromMySql(out SortedDictionary<string, string> map)
    {
        map = new SortedDictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var conn = OpenMySqlConnectionEnsuringDatabase();
            EnsureMySqlTable(conn);

            var sql = $"SELECT `{COL_SID}` AS sid, `{COL_NICK}` AS nick " +
                    $"FROM `{TBL}` WHERE `{COL_NICK}` IS NOT NULL AND `{COL_NICK}` <> ''";
            using var cmd = new MySqlCommand(sql, conn);
            using var r = cmd.ExecuteReader();

            while (r.Read())
            {
                var sid = r["sid"]?.ToString();
                var nick = r["nick"]?.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(sid) || string.IsNullOrWhiteSpace(nick)) continue;
                map[sid] = nick;
            }
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ForceNames] MySQL load error: {ex.Message}");
            return false;
        }
    }

    private bool UpsertMySqlMapping(string sid, string nickname)
    {
        try
        {
            using var conn = OpenMySqlConnectionEnsuringDatabase();
            EnsureMySqlTable(conn);
            var sql = $"INSERT INTO `{TBL}` (`{COL_SID}`,`{COL_NICK}`) VALUES (@sid,@nick) " +
                    $"ON DUPLICATE KEY UPDATE `{COL_NICK}`=VALUES(`{COL_NICK}`);";
            using var cmd = new MySqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@sid", sid);
            cmd.Parameters.AddWithValue("@nick", nickname);
            cmd.ExecuteNonQuery();
            return true;
        }
        catch (Exception ex) { Console.WriteLine($"[ForceNames] DB upsert error: {ex.Message}"); return false; }
    }

    private bool DeleteMySqlMapping(string sid)
    {
        try
        {
            using var conn = OpenMySqlConnectionEnsuringDatabase();
            EnsureMySqlTable(conn);
            var sql = $"DELETE FROM `{TBL}` WHERE `{COL_SID}`=@sid;";
            using var cmd = new MySqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@sid", sid);
            return cmd.ExecuteNonQuery() > 0;
        }
        catch (Exception ex) { Console.WriteLine($"[ForceNames] DB delete error: {ex.Message}"); return false; }
    }

}

public class ForceNamesConfig : BasePluginConfig
{
    public SortedDictionary<string, string> Mappings { get; set; } = new(StringComparer.Ordinal);
    public float ApplyIntervalSec { get; set; } = 10.0f;

    public bool UseMySql { get; set; } = false;
    public string DatabaseHost { get; set; } = "localhost";
    public int DatabasePort { get; set; } = 3306;
    public string DatabaseUser { get; set; } = "";
    public string DatabasePassword { get; set; } = "";
    public string DatabaseName { get; set; } = "cs2_players";

}
