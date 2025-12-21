# 🎀 CS2 ForceNames (SteamID → Nickname)

Map a player’s **SteamID64 → nickname** and keep their in-game name **locked** to it.
<br /> A lightweight periodic sweep (default **10s**) re-applies names so engine resets or other plugins can’t undo them.
<br /> Supports **JSON mode** (local config) and **MySQL mode** (recommended for large datasets).

## ✨ Features

* Force nicknames by **SteamID64**
* Periodic re-apply (interval configurable; defaults to 10s)
* Reload & apply from console or in-game chat
* **MySQL mode** (auto-creates DB/table if missing)

## 📦 File Layout

Place files under your CS2 server root:

```
csgo/
└─ game/
   └─ csgo/
      └─ addons/
         └─ counterstrikesharp/
            ├─ plugins/
            │  └─ ForceNames.dll          # plugins
            │  └─ MySqlConnector.dll      # required for MySQL mode
            └─ configs/
              └─ plugins/
                └─ ForceNames/
                  └─ Forcenames.json      # config
```

## ⚙️ Config (forcenames.json)

```json
{
  "mappings": {},
  "applyIntervalSec": 10,
  "useMySql": true,
  "databaseHost": "localhost",
  "databasePort": 3306,
  "databaseUser": "YOUR_USER",
  "databasePassword": "YOUR_PASSWORD",
  "databaseName": "DB_NAME"
}
```
* **applyIntervalSec**: Periodic sweep interval in seconds (10–30s recommended).
* **useMySql**: When *true*, the plugin loads mappings from MySQL and ignores *mappings* in JSON.
* **databaseName**: Database name to use (plugin will create it if missing).

## 🧰 Commands

```
!fn <steamid64> <nickname...>       # set / update mapping (JSON or MySQL)
!fn_unset <steamid64>               # remove mapping (JSON or MySQL)
!fn_reload                          # reload config + refresh mappings
!fn_list                            # print current mappings (cached)
```

## 🧪 Build

* Target: **.NET 8** / CounterStrikeSharp **v1.0.352+** / MySQL **v2.5.0**

## 📝 Changelog

```
## [1.1.0] - 2025-12-21
- Added MySQL mode (auto-create DB/table)
- Shortened commands to !fn_*
- Config path standardized to configs/plugins/ForceNames/ForceNames.json

## [1.0.0] - 2025-10-16
- Released
```

## 🙏 Credits
* Midori server ops team
* CounterStrikeSharp project & community

## 📄 License

* MIT
