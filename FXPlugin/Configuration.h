//+------------------------------------------------------------------+
//|                                         MetaTrader Trade Example |
//|                 Copyright © 2001-2010, MetaQuotes Software Corp. |
//|                                        http://www.metaquotes.net |
//+------------------------------------------------------------------+
#pragma once
//+------------------------------------------------------------------+
//| Simple synchronizer                                              |
//+------------------------------------------------------------------+
class CSync
{
private:
	CRITICAL_SECTION  m_cs;
public:
	CSync()  { ZeroMemory(&m_cs, sizeof(m_cs)); InitializeCriticalSection(&m_cs); }
	~CSync()  { DeleteCriticalSection(&m_cs);   ZeroMemory(&m_cs, sizeof(m_cs)); }
	inline void       Lock()   { EnterCriticalSection(&m_cs); }
	inline void       Unlock() { LeaveCriticalSection(&m_cs); }
};

class CSingleLock
{
private:
	CSync* m_Sync;
	CSingleLock() {};

public:
	static int m_sCount;

	CSingleLock(CSync* syncObject)
	{
		m_Sync = syncObject;
		m_Sync->Lock();
	}

	~CSingleLock()
	{
		m_Sync->Unlock();
	}
};

//+------------------------------------------------------------------+
//| Ñonfiguration                                                    |
//+------------------------------------------------------------------+
class CConfiguration
{
private:
	CSync             m_sync;           // synchronizer
	PluginCfg        *m_cfgs;           // parameters
	int               m_cfgs_total;     // count of parameters
	int               m_cfgs_max;       // maximum of parameters
	PluginCfg       **m_cfgs_index;     // index of parameters
	char              m_filename[256];  // configuration filename
public:
	CConfiguration();
	~CConfiguration();
	//---- load
	int               Load(const char *filename);
	//---- raw access
	int               Total(void);
	int               Add(const int pos, const PluginCfg *cfg);
	int               Delete(const char *name);
	int               Next(const int index, PluginCfg *cfg);
	int               Get(const char *name, PluginCfg *cfg, const int pos = 0);
	int               Set(const PluginCfg *cfgs, const int cfgs_total);
	//---- high-level access
	int               GetInteger(const int pos, const char *name, int *value, const char *defvalue = NULL);
	int               GetString(const int pos, const char *name, char *value, const int size, const char *defvalue = NULL);
	int               GetFloat(const int pos, const char *name, double *value, const char *defvalue = NULL);

private:
	//---- save
	int               Save(void);
	//---- sort functions
	static int        SortByName(const void *param1, const void *param2);
	static int        SortIndex(const void *param1, const void *param2);
};

extern CConfiguration ExtConfig;
//+------------------------------------------------------------------+
