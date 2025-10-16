<<<<<<< HEAD
﻿using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using CssTimer = CounterStrikeSharp.API.Modules.Timers.Timer;
using Microsoft.Extensions.Logging;
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
    public override string ModuleVersion => "1.0.0-Midori";
    public override string ModuleAuthor => "Midori";

    // CSS, JSON Config
    private ForceNamesConfig _cfg = new();
    private readonly JsonSerializerOptions _jsonOpt = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // Absolute Path
    private static string Abs(string p) => Path.GetFullPath(p);
    private string DesiredDir =>
        Abs(Path.Combine(ModuleDirectory, "..", "..", "configs", "plugins", "ForceNames"));
    private string DesiredPath =>
        Abs(Path.Combine(DesiredDir, "ForceNames.json"));

    // Load, Unload
    private CssTimer? _periodic;
    private bool _unloading;

    public override void Load(bool hotReload)
    {
        _unloading = false;

        EnsureConfig();
        StartPeriodicApply();

        AddCommandListener("say", OnSayCommand);
        AddCommandListener("say_team", OnSayCommand);

        AddCommand("css_forcename", "Force name by SteamID64", CmdForceName);
        AddCommand("css_unforcename", "Remove forced name", CmdUnforceName);
        AddCommand("css_forcenames_reload", "Reload config (prefers subfolder)", CmdReload);
        AddCommand("css_forcenames_list", "List mappings", CmdList);
    }

    public override void Unload(bool hotReload)
    {
        _unloading = true;
        _periodic?.Kill();
        _periodic = null;
    }

    // SchedulePeriod
    private void StartPeriodicApply()
    {
        var interval = Math.Clamp(_cfg?.ApplyIntervalSec ?? 10.0f, 1.0f, 60.0f);

        _periodic?.Kill();
        _periodic = AddTimer(interval, () =>
        {
            if (_unloading) return;
            try
            {
                ApplyAllOnlinePlayers();
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "[ForceNames] periodic apply failed");
            }
        }, TimerFlags.REPEAT);
    }

    private void ApplyAllOnlinePlayers()
    {
        foreach (var p in Utilities.GetPlayers())
        {
            if (p?.IsValid == true)
                TryApplyForcedName(p);
        }
    }

    // Chat Command Listener
    private HookResult OnSayCommand(CCSPlayerController? caller, CommandInfo cmd)
    {
        try
        {
            if (caller == null || !caller.IsValid) return HookResult.Continue;

            var msg = cmd.ArgCount >= 2 ? cmd.GetArg(1) : string.Empty;
            msg = msg.Trim().Trim('"').Trim();

            if (!msg.Equals("!forcenames_reload", StringComparison.OrdinalIgnoreCase) &&
                !msg.Equals("/forcenames_reload", StringComparison.OrdinalIgnoreCase))
                return HookResult.Continue;

            if (!AdminManager.PlayerHasPermissions(caller, "@css/root"))
            {
                cmd.ReplyToCommand("[ForceNames] You do not have permission.");
                return HookResult.Continue;
            }

            ReloadConfigAndApply(cmd);
            return HookResult.Continue;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ForceNames] OnSayCommand error: {ex}");
            return HookResult.Continue;
        }
    }

    // Reload + Apply Config
    private void ReloadConfigAndApply(CommandInfo cmd)
    {
        if (!File.Exists(DesiredPath))
        {
            SaveConfig();
            cmd.ReplyToCommand("[ForceNames] Created new config.");
            return;
        }

        try
        {
            var json = File.ReadAllText(DesiredPath);
            _cfg = JsonSerializer.Deserialize<ForceNamesConfig>(json, _jsonOpt) ?? _cfg;

            SaveConfig();
            ApplyAllOnlinePlayers();
            StartPeriodicApply();

            cmd.ReplyToCommand("[ForceNames] Reloaded & applied.");
        }
        catch (Exception ex)
        {
            cmd.ReplyToCommand($"[ForceNames] Reload failed: {ex.Message}");
        }
    }

    //Core
    private void TryApplyForcedName(CCSPlayerController player)
    {
        try
        {
            if (player == null || !player.IsValid || player.IsBot) return;

            var steamId64 = player.SteamID.ToString();

            if (_cfg.Mappings != null && _cfg.Mappings.TryGetValue(steamId64, out var forced))
            {
                var current = player.PlayerName ?? string.Empty;
                if (string.Equals(current, forced, StringComparison.Ordinal))
                    return;

                player.PlayerName = forced;
                try { Utilities.SetStateChanged(player, "CCSPlayerController", "m_iszPlayerName"); } catch { }
                if (_cfg.LogApply) Console.WriteLine($"[ForceNames] Applied '{forced}' to {steamId64} (was '{current}')");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ForceNames] TryApplyForcedName error: {ex}");
        }
    }

    [GameEventHandler]
    public HookResult OnPlayerFullConnect(EventPlayerConnectFull e, GameEventInfo info)
    {
        var p = e.Userid;
        if (p?.IsValid == true) TryApplyForcedName(p);
        return HookResult.Continue;
    }

    // Commands
    [ConsoleCommand("css_forcename")]
    private void CmdForceName(CCSPlayerController? caller, CommandInfo cmd)
    {
        if (caller != null && !AdminManager.PlayerHasPermissions(caller, "@css/root"))
        { cmd.ReplyToCommand("You do not have permission."); return; }

        if (cmd.ArgCount < 3)
        { cmd.ReplyToCommand("Usage: css_forcename <steamid64> <nickname...>"); return; }

        var sid = cmd.GetArg(1);
        var parts = new List<string>();
        for (int i = 2; i < cmd.ArgCount; i++) parts.Add(cmd.GetArg(i));
        var nickname = string.Join(' ', parts);

        _cfg.Mappings[sid] = nickname;
        SaveConfig();
        cmd.ReplyToCommand($"[ForceNames] Mapped {sid} => '{nickname}' (saved)");

        if (ulong.TryParse(sid, out var sid64))
        {
            var target = Utilities.GetPlayerFromSteamId(sid64);
            if (target?.IsValid == true) TryApplyForcedName(target);
        }
    }

    [ConsoleCommand("css_unforcename")]
    private void CmdUnforceName(CCSPlayerController? caller, CommandInfo cmd)
    {
        if (caller != null && !AdminManager.PlayerHasPermissions(caller, "@css/root"))
        { cmd.ReplyToCommand("You do not have permission."); return; }

        if (cmd.ArgCount < 2)
        { cmd.ReplyToCommand("Usage: css_unforcename <steamid64>"); return; }

        var sid = cmd.GetArg(1);
        if (_cfg.Mappings.Remove(sid))
        {
            SaveConfig();
            cmd.ReplyToCommand($"[ForceNames] Removed {sid} (saved)");
        }
        else cmd.ReplyToCommand($"[ForceNames] No mapping for {sid}");
    }

    [ConsoleCommand("css_forcenames_reload")]
    private void CmdReload(CCSPlayerController? caller, CommandInfo cmd)
    {
        if (caller != null && !AdminManager.PlayerHasPermissions(caller, "@css/root"))
        { cmd.ReplyToCommand("You do not have permission."); return; }

        if (!File.Exists(DesiredPath)) { SaveConfig(); cmd.ReplyToCommand("[ForceNames] Created new config."); return; }
        try
        {
            var json = File.ReadAllText(DesiredPath);
            _cfg = JsonSerializer.Deserialize<ForceNamesConfig>(json, _jsonOpt) ?? _cfg;

            SaveConfig();
            cmd.ReplyToCommand("[ForceNames] Reloaded.");
        }
        catch (Exception ex)
        {
            cmd.ReplyToCommand($"[ForceNames] Reload failed: {ex.Message}");
        }
    }

    [ConsoleCommand("css_forcenames_list")]
    private void CmdList(CCSPlayerController? caller, CommandInfo cmd)
    {
        if (caller != null && !AdminManager.PlayerHasPermissions(caller, "@css/root"))
        { cmd.ReplyToCommand("You do not have permission."); return; }

        if (_cfg.Mappings.Count == 0) { cmd.ReplyToCommand("[ForceNames] (empty)"); return; }
        foreach (var kv in _cfg.Mappings) cmd.ReplyToCommand($"{kv.Key} => {kv.Value}");
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
        var tmp = DesiredPath + ".tmp"; //use temp
        File.WriteAllText(tmp, json);
        File.Move(tmp, DesiredPath, true);
    }
}
public class ForceNamesConfig
{
    public SortedDictionary<string, string> Mappings { get; set; } = new(StringComparer.Ordinal);
    public bool LogApply { get; set; } = true;
    public float ApplyIntervalSec { get; set; } = 10.0f;
}
=======
﻿using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using CssTimer = CounterStrikeSharp.API.Modules.Timers.Timer;
using Microsoft.Extensions.Logging;
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
    public override string ModuleVersion => "1.0.0-Midori";
    public override string ModuleAuthor => "Midori";

    // CSS, JSON Config
    private ForceNamesConfig _cfg = new();
    private readonly JsonSerializerOptions _jsonOpt = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // Absolute Path
    private static string Abs(string p) => Path.GetFullPath(p);
    private string DesiredDir =>
        Abs(Path.Combine(ModuleDirectory, "..", "..", "configs", "plugins", "ForceNames"));
    private string DesiredPath =>
        Abs(Path.Combine(DesiredDir, "ForceNames.json"));

    // Load, Unload
    private CssTimer? _periodic;
    private bool _unloading;

    public override void Load(bool hotReload)
    {
        _unloading = false;

        EnsureConfig();
        StartPeriodicApply();

        AddCommandListener("say", OnSayCommand);
        AddCommandListener("say_team", OnSayCommand);

        AddCommand("css_forcename", "Force name by SteamID64", CmdForceName);
        AddCommand("css_unforcename", "Remove forced name", CmdUnforceName);
        AddCommand("css_forcenames_reload", "Reload config (prefers subfolder)", CmdReload);
        AddCommand("css_forcenames_list", "List mappings", CmdList);
    }

    public override void Unload(bool hotReload)
    {
        _unloading = true;
        _periodic?.Kill();
        _periodic = null;
    }

    // SchedulePeriod
    private void StartPeriodicApply()
    {
        var interval = Math.Clamp(_cfg?.ApplyIntervalSec ?? 10.0f, 1.0f, 60.0f);

        _periodic?.Kill();
        _periodic = AddTimer(interval, () =>
        {
            if (_unloading) return;
            try
            {
                ApplyAllOnlinePlayers();
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "[ForceNames] periodic apply failed");
            }
        }, TimerFlags.REPEAT);
    }

    private void ApplyAllOnlinePlayers()
    {
        foreach (var p in Utilities.GetPlayers())
        {
            if (p?.IsValid == true)
                TryApplyForcedName(p);
        }
    }

    // Chat Command Listener
    private HookResult OnSayCommand(CCSPlayerController? caller, CommandInfo cmd)
    {
        try
        {
            if (caller == null || !caller.IsValid) return HookResult.Continue;

            var msg = cmd.ArgCount >= 2 ? cmd.GetArg(1) : string.Empty;
            msg = msg.Trim().Trim('"').Trim();

            if (!msg.Equals("!forcenames_reload", StringComparison.OrdinalIgnoreCase) &&
                !msg.Equals("/forcenames_reload", StringComparison.OrdinalIgnoreCase))
                return HookResult.Continue;

            if (!AdminManager.PlayerHasPermissions(caller, "@css/root"))
            {
                cmd.ReplyToCommand("[ForceNames] You do not have permission.");
                return HookResult.Continue;
            }

            ReloadConfigAndApply(cmd);
            return HookResult.Continue;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ForceNames] OnSayCommand error: {ex}");
            return HookResult.Continue;
        }
    }

    // Reload + Apply Config
    private void ReloadConfigAndApply(CommandInfo cmd)
    {
        if (!File.Exists(DesiredPath))
        {
            SaveConfig();
            cmd.ReplyToCommand("[ForceNames] Created new config.");
            return;
        }

        try
        {
            var json = File.ReadAllText(DesiredPath);
            _cfg = JsonSerializer.Deserialize<ForceNamesConfig>(json, _jsonOpt) ?? _cfg;

            SaveConfig();
            ApplyAllOnlinePlayers();
            StartPeriodicApply();

            cmd.ReplyToCommand("[ForceNames] Reloaded & applied.");
        }
        catch (Exception ex)
        {
            cmd.ReplyToCommand($"[ForceNames] Reload failed: {ex.Message}");
        }
    }

    //Core
    private void TryApplyForcedName(CCSPlayerController player)
    {
        try
        {
            if (player == null || !player.IsValid || player.IsBot) return;

            var steamId64 = player.SteamID.ToString();

            if (_cfg.Mappings != null && _cfg.Mappings.TryGetValue(steamId64, out var forced))
            {
                var current = player.PlayerName ?? string.Empty;
                if (string.Equals(current, forced, StringComparison.Ordinal))
                    return;

                player.PlayerName = forced;
                try { Utilities.SetStateChanged(player, "CCSPlayerController", "m_iszPlayerName"); } catch { }
                if (_cfg.LogApply) Console.WriteLine($"[ForceNames] Applied '{forced}' to {steamId64} (was '{current}')");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ForceNames] TryApplyForcedName error: {ex}");
        }
    }

    [GameEventHandler]
    public HookResult OnPlayerFullConnect(EventPlayerConnectFull e, GameEventInfo info)
    {
        var p = e.Userid;
        if (p?.IsValid == true) TryApplyForcedName(p);
        return HookResult.Continue;
    }

    // Commands
    [ConsoleCommand("css_forcename")]
    private void CmdForceName(CCSPlayerController? caller, CommandInfo cmd)
    {
        if (caller != null && !AdminManager.PlayerHasPermissions(caller, "@css/root"))
        { cmd.ReplyToCommand("You do not have permission."); return; }

        if (cmd.ArgCount < 3)
        { cmd.ReplyToCommand("Usage: css_forcename <steamid64> <nickname...>"); return; }

        var sid = cmd.GetArg(1);
        var parts = new List<string>();
        for (int i = 2; i < cmd.ArgCount; i++) parts.Add(cmd.GetArg(i));
        var nickname = string.Join(' ', parts);

        _cfg.Mappings[sid] = nickname;
        SaveConfig();
        cmd.ReplyToCommand($"[ForceNames] Mapped {sid} => '{nickname}' (saved)");

        if (ulong.TryParse(sid, out var sid64))
        {
            var target = Utilities.GetPlayerFromSteamId(sid64);
            if (target?.IsValid == true) TryApplyForcedName(target);
        }
    }

    [ConsoleCommand("css_unforcename")]
    private void CmdUnforceName(CCSPlayerController? caller, CommandInfo cmd)
    {
        if (caller != null && !AdminManager.PlayerHasPermissions(caller, "@css/root"))
        { cmd.ReplyToCommand("You do not have permission."); return; }

        if (cmd.ArgCount < 2)
        { cmd.ReplyToCommand("Usage: css_unforcename <steamid64>"); return; }

        var sid = cmd.GetArg(1);
        if (_cfg.Mappings.Remove(sid))
        {
            SaveConfig();
            cmd.ReplyToCommand($"[ForceNames] Removed {sid} (saved)");
        }
        else cmd.ReplyToCommand($"[ForceNames] No mapping for {sid}");
    }

    [ConsoleCommand("css_forcenames_reload")]
    private void CmdReload(CCSPlayerController? caller, CommandInfo cmd)
    {
        if (caller != null && !AdminManager.PlayerHasPermissions(caller, "@css/root"))
        { cmd.ReplyToCommand("You do not have permission."); return; }

        if (!File.Exists(DesiredPath)) { SaveConfig(); cmd.ReplyToCommand("[ForceNames] Created new config."); return; }
        try
        {
            var json = File.ReadAllText(DesiredPath);
            _cfg = JsonSerializer.Deserialize<ForceNamesConfig>(json, _jsonOpt) ?? _cfg;

            SaveConfig();
            cmd.ReplyToCommand("[ForceNames] Reloaded.");
        }
        catch (Exception ex)
        {
            cmd.ReplyToCommand($"[ForceNames] Reload failed: {ex.Message}");
        }
    }

    [ConsoleCommand("css_forcenames_list")]
    private void CmdList(CCSPlayerController? caller, CommandInfo cmd)
    {
        if (caller != null && !AdminManager.PlayerHasPermissions(caller, "@css/root"))
        { cmd.ReplyToCommand("You do not have permission."); return; }

        if (_cfg.Mappings.Count == 0) { cmd.ReplyToCommand("[ForceNames] (empty)"); return; }
        foreach (var kv in _cfg.Mappings) cmd.ReplyToCommand($"{kv.Key} => {kv.Value}");
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
        var tmp = DesiredPath + ".tmp"; //use temp
        File.WriteAllText(tmp, json);
        File.Move(tmp, DesiredPath, true);
    }
}
public class ForceNamesConfig
{
    public SortedDictionary<string, string> Mappings { get; set; } = new(StringComparer.Ordinal);
    public bool LogApply { get; set; } = true;
    public float ApplyIntervalSec { get; set; } = 10.0f;
}
>>>>>>> 47952f0 (Create ForceNamesPlugin.cs)
