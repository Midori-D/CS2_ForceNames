#ifndef _INCLUDE_METAMOD_SOURCE_FORCENAMES_H_
#define _INCLUDE_METAMOD_SOURCE_FORCENAMES_H_

#include <ISmmPlugin.h>
#include "iserver.h"

class ForceNames : public ISmmPlugin, public IMetamodListener
{
public:
	bool Load(PluginId id, ISmmAPI* ismm, char* error, size_t maxlen, bool late);
	bool Unload(char* error, size_t maxlen);

	bool Pause(char* error, size_t maxlen);
	bool Unpause(char* error, size_t maxlen);
	void AllPluginsLoaded();

	void Hook_ClientSettingsChanged(CPlayerSlot slot);
	void Hook_GameServerSteamAPIActivated();
	void Hook_GameServerSteamAPIDeactivated();
	void Hook_GameFrame(bool simulating, bool bFirstTick, bool bLastTick);

	void OnLevelInit(const char* pMapName, const char* pMapEntities, const char* pOldLevel, const char* pLandmarkName, bool loadGame, bool background);
	void OnLevelShutdown();

	void UpdatePlayers();

public:
	const char* GetAuthor();
	const char* GetName();
	const char* GetDescription();
	const char* GetURL();
	const char* GetLicense();
	const char* GetVersion();
	const char* GetDate();
	const char* GetLogTag();
};

extern ForceNames g_ForceNames;
PLUGIN_GLOBALVARS();

#endif
