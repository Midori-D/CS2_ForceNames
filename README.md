# 🎀 CS2 ForceNames (SteamID → Nickname)

Map a player’s **SteamID64 → nickname** and keep their in-game name **locked** to it.
<br /> A lightweight periodic sweep (default **10s**) re-applies names so engine resets or other plugins can’t undo them.

## ✨ Features

* Force nicknames by **SteamID64**
* Periodic re-apply (interval configurable; defaults to 10s)
* Reload & apply from console or in-game chat

## 📦 File Layout

Place files under your CS2 server root:

```
csgo/
└─ game/
   └─ csgo/
      └─ addons/
         └─ counterstrikesharp/
            ├─ plugins/
            │  └─ ForceNames.dll          # build output 
            └─ configs/
              └─ plugins/
                └─ ForceNames/
                  └─ Forcenames.json      # mapping
```

## ⚙️ Config (forcenames.json)

```json
{
  "mappings": {
    "76561198000000001": "Midori",
    "76561198000000002": "Dakdori"
  },
  "logApply": false,
  "applyIntervalSec": 10.0
}
```

* **Mappings** : Dictionary of "SteamID64": "Nickname" (Use string SteamID64 (17-digit) as keys).
* **LogApply** : When true, logs a line on each actual nickname change.
* **applyIntervalSec** : Periodic sweep interval in seconds (5–10s recommended).

## 🧰 Commands

```
css_forcename <steamid64> <nickname...>   # set or update mapping (persists)
css_unforcename <steamid64>               # remove mapping (persists)
css_forcenames_reload                     # reload JSON
css_forcenames_list                       # print current mappings
```
* In-game chat (admin): !forcenames_reload or /forcenames_reload (reload & apply)
* Permission: default **@css/root**. Adjust as you like.

## 🧪 Build

* Target: **.NET 8** / CounterStrikeSharp **v1.0.340+**

## 📝 Changelog

```
## [1.0.0] - 2025-10-16
- Released
```

## 🙏 Credits
* Midori server ops team
* CounterStrikeSharp project & community

## 📄 License

* MIT
