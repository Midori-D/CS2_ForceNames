# 🎀 CS2 ForceNames [SteamID64 → Nickname]

Map a player’s **SteamID64 → nickname** and keep their in-game name **locked** to it.
<br /> A lightweight periodic sweep re-applies names so engine resets or other plugins can’t undo them.
<br /> ~~Supports **JSON mode** (local config) and **MySQL mode** (recommended for large datasets).~~
<br /> 
<br /> ![ForceNames](/forcenames.gif)

## ✨ Features

* Force nicknames by **SteamID64**
* Stable nickname re-apply logic
* ~~Reload & apply from console or in-game chat~~
* ~~**MySQL mode** (auto-creates DB/table if missing)~~

## 📦 File Layout

Place files under your CS2 server root:

```
csgo/
├─ addons/
│  ├─ metamod/
│  │  └─ forcenames.vdf
│  └─ forcenames/
│     └─ bin/
│        └─ linuxsteamrt64/
│           └─ forcenames.so
└─ cfg/
   └─ forcenames/
      └─ forcenames.cfg
```

## ⚙️ Config (forcenames.json)

```json
~~{
  "mappings": {},
  "applyIntervalSec": 10,
  "useMySql": true,
  "databaseHost": "localhost",
  "databasePort": 3306,
  "databaseUser": "YOUR_USER",
  "databasePassword": "YOUR_PASSWORD",
  "databaseName": "DB_NAME"
}~~
```
~~* **applyIntervalSec**: Periodic sweep interval in seconds.~~
~~* **useMySql**: When `true`, the plugin loads mappings from MySQL and ignores *mappings* in JSON.~~
~~* **databaseName**: Database name to use (plugin will create it if missing).~~

## 🧰 Commands

```
~~!fn <steamid64|#idx|name> <nickname>       # set / update mapping (JSON or MySQL)
!unfn <steamid64|#idx|name>                # remove mapping (JSON or MySQL)
!fn_reload                                 # reload config + refresh mappings
!fn_list                                   # print current mappings (cached)
!fn_players                                # list online players with indexes~~
```

## 🧪 Notes

* This plugin is a **MetaMod:Source plugin**, not a CounterStrikeSharp plugin.
* Nickname forcing is designed around the in-game player name path.
* ~~JSON mode~~
* ~~MySQL mode~~
  
## 🚀 Build

* Platform: **Linux / linuxsteamrt64**
* Target: **CS2 MetaMod:Source plugin**

## 📝 Changelog

```
## [2.0.0] - 2026-04-02
- Rebuilt as a MetaMod:Source plugin for CS2
- Added fn_reloadcfg command
- Improved nickname stability in-game

## [1.2.0] - 2026-01-24
- Added !fn_players
- Flexible target selector for !fn/!unfn (SteamID64/#idx/name)
- Code optimizations

## [1.1.1] - 2025-12-22
- Hotfix (ChatColor)

## [1.1.0] - 2025-12-21
- Added MySQL mode (auto-create DB/table)
- Shortened commands to !fn_*
- Config path standardized to configs/plugins/ForceNames/ForceNames.json

## [1.0.0] - 2025-10-16
- Released
```

## 🙏 Credits
* Midori server ops team
* MetaMod:Source / AlliedModders community

## 📄 License

* MIT
