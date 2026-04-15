#include "forcenames.h"

#include <steam/steam_gameserver.h>

#include <algorithm>
#include <cctype>
#include <cerrno>
#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <fstream>
#include <sstream>
#include <string>
#include <unordered_map>

#include "tier1/convar.h"
#include "utils/module.h"
#include "schemasystem/schemasystem.h"
#include "cs2_sdk/entity/cbaseplayercontroller.h"

class GameSessionConfiguration_t { };

SH_DECL_HOOK3_void(IServerGameDLL, GameFrame, SH_NOATTRIB, 0, bool, bool, bool);
SH_DECL_HOOK0_void(IServerGameDLL, GameServerSteamAPIActivated, SH_NOATTRIB, 0);
SH_DECL_HOOK0_void(IServerGameDLL, GameServerSteamAPIDeactivated, SH_NOATTRIB, 0);
SH_DECL_HOOK1_void(IServerGameClients, ClientSettingsChanged, SH_NOATTRIB, 0, CPlayerSlot);

ForceNames g_ForceNames;
PLUGIN_EXPOSE(ForceNames, g_ForceNames);

ICvar* icvar = nullptr;
IServerGameDLL* server = nullptr;
IVEngineServer* engine = nullptr;
IServerGameClients* gameclients = nullptr;
CSteamGameServerAPIContext g_steamAPI = {};
CGameEntitySystem* g_pEntitySystem = nullptr;

static std::unordered_map<uint64_t, std::string> g_ForcedNames;
static std::string g_ForceNamesCfgPath;

#ifdef _WIN32
static constexpr int kGameEntitySystemOffset = 88;
#else
static constexpr int kGameEntitySystemOffset = 80;
#endif

static std::string Trim(std::string s)
{
    auto notSpace = [](unsigned char ch) { return !std::isspace(ch); };

    s.erase(s.begin(), std::find_if(s.begin(), s.end(), notSpace));
    s.erase(std::find_if(s.rbegin(), s.rend(), notSpace).base(), s.end());

    return s;
}

static std::string StripQuotes(std::string s)
{
    s = Trim(std::move(s));

    if (s.size() >= 2)
    {
        if ((s.front() == '"' && s.back() == '"') ||
            (s.front() == '\'' && s.back() == '\''))
        {
            s = s.substr(1, s.size() - 2);
        }
    }

    return s;
}

static bool IsCommentOrEmpty(const std::string& raw)
{
    std::string s = Trim(raw);

    if (s.empty())
        return true;

    if (s[0] == '#' || s[0] == ';')
        return true;

    if (s.size() >= 2 && s[0] == '/' && s[1] == '/')
        return true;

    return false;
}

static bool ParseForcedNameLine(const std::string& rawLine, uint64_t& outSteam64, std::string& outName)
{
    if (IsCommentOrEmpty(rawLine))
        return false;

    std::istringstream iss(rawLine);

    std::string sidText;
    if (!(iss >> sidText))
        return false;

    errno = 0;
    char* endPtr = nullptr;
    unsigned long long sid = std::strtoull(sidText.c_str(), &endPtr, 10);

    if (errno != 0 || endPtr == sidText.c_str() || *endPtr != '\0')
        return false;

    std::string rest;
    std::getline(iss, rest);
    rest = StripQuotes(rest);

    if (rest.empty())
        return false;

    outSteam64 = static_cast<uint64_t>(sid);
    outName = rest;
    return true;
}

static bool LoadForcedNamesFromCfg(const char* path)
{
    std::ifstream file(path);
    if (!file.is_open())
        return false;

    std::unordered_map<uint64_t, std::string> newMap;

    std::string line;
    while (std::getline(file, line))
    {
        uint64_t steam64 = 0;
        std::string forcedName;

        if (!ParseForcedNameLine(line, steam64, forcedName))
            continue;

        newMap[steam64] = forcedName;
    }

    g_ForcedNames.swap(newMap);
    return true;
}

static void ForceNamesReloadCfgCommand(const CCommandContext& context, const CCommand& args)
{
    if (LoadForcedNamesFromCfg(g_ForceNamesCfgPath.c_str()))
    {
        g_SMAPI->ConPrintf(
            "[ForceNames] loaded %zu entries from %s\n",
            g_ForcedNames.size(),
            g_ForceNamesCfgPath.c_str()
        );
    }
    else
    {
        g_SMAPI->ConPrintf(
            "[ForceNames] failed to open %s\n",
            g_ForceNamesCfgPath.c_str()
        );
    }
}

static ConCommand s_fnReloadCfg(
    "fn_reloadcfg",
    ForceNamesReloadCfgCommand,
    "Reloads ForceNames config from cfg/forcenamesmm/forcenames.cfg",
    FCVAR_RELEASE
);

CGameEntitySystem* GameEntitySystem()
{
    return *reinterpret_cast<CGameEntitySystem**>(
        reinterpret_cast<uintptr_t>(g_pGameResourceServiceServer) + kGameEntitySystemOffset
    );
}

static CBasePlayerController* GetControllerFromSlot(int slotIndex)
{
    if (!g_pEntitySystem)
        return nullptr;

    return reinterpret_cast<CBasePlayerController*>(
        g_pEntitySystem->GetEntityInstance(CEntityIndex(slotIndex + 1))
    );
}

static const char* GetForcedNameForSlot(CPlayerSlot slot, CBasePlayerController* controller)
{
    auto steamId = engine->GetClientSteamID(slot);
    if (!steamId)
        return controller && controller->GetPlayerName() ? controller->GetPlayerName() : "";

    auto it = g_ForcedNames.find(steamId->ConvertToUint64());
    if (it != g_ForcedNames.end() && !it->second.empty())
        return it->second.c_str();

    return controller && controller->GetPlayerName() ? controller->GetPlayerName() : "";
}

static bool TryForceControllerName(CBasePlayerController* controller, const char* forcedName)
{
    if (!controller || !forcedName)
        return false;

    const char* current = controller->GetPlayerName();
    if (!current)
        return false;

    if (std::strcmp(current, forcedName) == 0)
        return true;

    constexpr size_t kMaxSafeCopy = 127;
    size_t len = std::strlen(forcedName);
    if (len > kMaxSafeCopy)
        len = kMaxSafeCopy;

    char* writable = const_cast<char*>(current);
    std::memcpy(writable, forcedName, len);
    writable[len] = '\0';

    return true;
}

static void UpdateSteamUserData(CPlayerSlot slot, const char* finalName)
{
    if (!g_steamAPI.SteamGameServer() || !finalName)
        return;

    auto steamId = engine->GetClientSteamID(slot);
    if (!steamId)
        return;

    const int score = gameclients->GetPlayerScore(slot);
    g_steamAPI.SteamGameServer()->BUpdateUserData(*steamId, finalName, score);
}

bool ForceNames::Load(PluginId id, ISmmAPI* ismm, char* error, size_t maxlen, bool late)
{
    PLUGIN_SAVEVARS();

    GET_V_IFACE_CURRENT(GetEngineFactory, icvar, ICvar, CVAR_INTERFACE_VERSION);
    GET_V_IFACE_ANY(GetServerFactory, g_pSource2Server, ISource2Server, SOURCE2SERVER_INTERFACE_VERSION);
    GET_V_IFACE_ANY(GetServerFactory, server, IServerGameDLL, INTERFACEVERSION_SERVERGAMEDLL);
    GET_V_IFACE_CURRENT(GetEngineFactory, engine, IVEngineServer, INTERFACEVERSION_VENGINESERVER);
    GET_V_IFACE_ANY(GetServerFactory, gameclients, IServerGameClients, INTERFACEVERSION_SERVERGAMECLIENTS);
    GET_V_IFACE_CURRENT(GetEngineFactory, g_pSchemaSystem, ISchemaSystem, SCHEMASYSTEM_INTERFACE_VERSION);
    GET_V_IFACE_ANY(GetEngineFactory, g_pNetworkServerService, INetworkServerService, NETWORKSERVERSERVICE_INTERFACE_VERSION);
    GET_V_IFACE_CURRENT(GetEngineFactory, g_pGameResourceServiceServer, IGameResourceService, GAMERESOURCESERVICESERVER_INTERFACE_VERSION);

    g_SMAPI->AddListener(this, this);

    SH_ADD_HOOK(IServerGameDLL, GameFrame, server, SH_MEMBER(this, &ForceNames::Hook_GameFrame), true);
    SH_ADD_HOOK(IServerGameDLL, GameServerSteamAPIActivated, g_pSource2Server, SH_MEMBER(this, &ForceNames::Hook_GameServerSteamAPIActivated), false);
    SH_ADD_HOOK(IServerGameDLL, GameServerSteamAPIDeactivated, g_pSource2Server, SH_MEMBER(this, &ForceNames::Hook_GameServerSteamAPIDeactivated), false);
    SH_ADD_HOOK(IServerGameClients, ClientSettingsChanged, gameclients, SH_MEMBER(this, &ForceNames::Hook_ClientSettingsChanged), true);

    g_pCVar = icvar;
    ConVar_Register(FCVAR_RELEASE | FCVAR_CLIENT_CAN_EXECUTE | FCVAR_GAMEDLL);

    // 경로 합성
    char szPath[512];
    snprintf(szPath, sizeof(szPath), "%s/cfg/forcenames/forcenames.cfg", g_SMAPI->GetBaseDir());
    g_ForceNamesCfgPath = szPath;
    LoadForcedNamesFromCfg(g_ForceNamesCfgPath.c_str());

    return true;
}

bool ForceNames::Unload(char* error, size_t maxlen)
{
    SH_REMOVE_HOOK(IServerGameDLL, GameFrame, server, SH_MEMBER(this, &ForceNames::Hook_GameFrame), true);
    SH_REMOVE_HOOK(IServerGameDLL, GameServerSteamAPIActivated, g_pSource2Server, SH_MEMBER(this, &ForceNames::Hook_GameServerSteamAPIActivated), false);
    SH_REMOVE_HOOK(IServerGameDLL, GameServerSteamAPIDeactivated, g_pSource2Server, SH_MEMBER(this, &ForceNames::Hook_GameServerSteamAPIDeactivated), false);
    SH_REMOVE_HOOK(IServerGameClients, ClientSettingsChanged, gameclients, SH_MEMBER(this, &ForceNames::Hook_ClientSettingsChanged), true);

    return true;
}

void ForceNames::Hook_GameServerSteamAPIActivated()
{
    g_steamAPI.Init();
}

void ForceNames::Hook_GameServerSteamAPIDeactivated()
{
}

void ForceNames::Hook_ClientSettingsChanged(CPlayerSlot slot)
{
    if (!engine || !gameclients)
        return;

    g_pEntitySystem = GameEntitySystem();
    if (!g_pEntitySystem)
        return;

    int idx = slot.Get();
    if (idx < 0)
        return;

    auto controller = GetControllerFromSlot(idx);
    if (!controller)
        return;

    const char* forced = GetForcedNameForSlot(slot, controller);
    if (!forced || !forced[0])
        return;

    TryForceControllerName(controller, forced);
    UpdateSteamUserData(slot, forced);
}

void ForceNames::UpdatePlayers()
{
    if (!engine || !gameclients)
        return;

    auto gpGlobals = engine->GetServerGlobals();
    if (!gpGlobals)
        return;

    g_pEntitySystem = GameEntitySystem();
    if (!g_pEntitySystem)
        return;

    for (int i = 0; i < gpGlobals->maxClients; i++)
    {
        CPlayerSlot slot(i);

        auto steamId = engine->GetClientSteamID(slot);
        if (!steamId)
            continue;

        auto controller = GetControllerFromSlot(i);
        if (!controller)
            continue;

        const char* finalName = GetForcedNameForSlot(slot, controller);

        TryForceControllerName(controller, finalName);
        UpdateSteamUserData(slot, finalName);
    }
}

void ForceNames::Hook_GameFrame(bool simulating, bool bFirstTick, bool bLastTick)
{
    static double nextUpdate = 0.0;

    const double curtime = Plat_FloatTime();
    if (curtime < nextUpdate)
        return;

    UpdatePlayers();

    nextUpdate = curtime + 0.5;
}

void ForceNames::AllPluginsLoaded()
{
}

void ForceNames::OnLevelInit(const char* pMapName, const char* pMapEntities, const char* pOldLevel, const char* pLandmarkName, bool loadGame, bool background)
{
}

void ForceNames::OnLevelShutdown()
{
}

bool ForceNames::Pause(char* error, size_t maxlen)
{
    return true;
}

bool ForceNames::Unpause(char* error, size_t maxlen)
{
    return true;
}

const char* ForceNames::GetLicense()
{
    return "GPLv3";
}

const char* ForceNames::GetVersion()
{
    return "2.0.0";
}

const char* ForceNames::GetDate()
{
    return __DATE__;
}

const char* ForceNames::GetLogTag()
{
    return "ForceNames";
}

const char* ForceNames::GetAuthor()
{
    return "Midori";
}

const char* ForceNames::GetDescription()
{
    return "Forces selected player names from cfg into controller data and Steam server user data.";
}

const char* ForceNames::GetName()
{
    return "ForceNames";
}

const char* ForceNames::GetURL()
{
    return "https://ataks.kr";
}
