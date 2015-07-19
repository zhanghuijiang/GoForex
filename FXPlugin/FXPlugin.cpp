//+------------------------------------------------------------------+
//|                                            MetaTrader Server API |
//|                   Copyright 2001-2014, MetaQuotes Software Corp. |
//|                                        http://www.metaquotes.net |
//+------------------------------------------------------------------+
#include "stdafx.h"
#include "Processor.h"
//---
PluginInfo        ExtPluginInfo = { "FXGlobal Extension", 100, "FXGlobal", { 0 } };
CServerInterface *ExtServer = NULL;
//+------------------------------------------------------------------+
//| DLL entry point                                                  |
//+------------------------------------------------------------------+
BOOL APIENTRY DllMain( HMODULE hModule,
                       DWORD  ul_reason_for_call,
                       LPVOID lpReserved
					 )
{
	char tmp[256], *cp;
	switch (ul_reason_for_call)
	{
	case DLL_PROCESS_ATTACH:
		//---- create configuration file name
		GetModuleFileName((HMODULE)hModule, tmp, sizeof(tmp)-1);
		if ((cp = strrchr(tmp, '.')) != NULL) *cp = 0;
		//---- add .ini
		strcat(tmp, ".ini");
		//---- load configuration
		ExtConfig.Load(tmp);
		break;
	case DLL_THREAD_ATTACH:
	case DLL_THREAD_DETACH:
	case DLL_PROCESS_DETACH:
		break;
	}
	return TRUE;
}

//+------------------------------------------------------------------+
//| About, must be present always!                                   |
//+------------------------------------------------------------------+
void APIENTRY MtSrvAbout(PluginInfo *info)
{
	if (info != NULL) memcpy(info, &ExtPluginInfo, sizeof(PluginInfo));
}
//+------------------------------------------------------------------+
//| Set server interface point                                       |
//+------------------------------------------------------------------+
int APIENTRY MtSrvStartup(CServerInterface *server)
{
	//--- check version
	if (server == NULL)                        return(FALSE);
	if (server->Version() != ServerApiVersion) return(FALSE);
	//--- save server interface link
	ExtServer = server;
	ExtServer->LogsOut(CmdOK, "FXGlobal Extension", "MtSrvStartup");
	//---
	//---- initialize processor
	ExtProcessor.Initialize();
	return(TRUE);
}

void APIENTRY MtSrvCleanup(void)
{
	ExtServer->LogsOut(CmdOK, "FXGlobal Extension", "MtSrvCleanup");
	ExtProcessor.Terminate();
	ExtServer->LogsOut(CmdOK, "FXGlobal Extension", "MtSrvCleanup Finished");
}

//+------------------------------------------------------------------+
//| Standard configuration functions                                 |
//+------------------------------------------------------------------+
int APIENTRY MtSrvPluginCfgAdd(const PluginCfg *cfg)
{
	int res = ExtConfig.Add(0, cfg);
	ExtProcessor.Initialize();
	return(res);
}
int APIENTRY MtSrvPluginCfgSet(const PluginCfg *values, const int total)
{
	
	int res = ExtConfig.Set(values, total);
	ExtProcessor.Initialize();
	return(res);
}
int APIENTRY MtSrvPluginCfgDelete(LPCSTR name)
{
	int res = ExtConfig.Delete(name);
	ExtProcessor.Initialize();
	return(res);
}
int APIENTRY MtSrvPluginCfgGet(LPCSTR name, PluginCfg *cfg)      { return ExtConfig.Get(name, cfg); }
int APIENTRY MtSrvPluginCfgNext(const int index, PluginCfg *cfg) { return ExtConfig.Next(index, cfg); }
int APIENTRY MtSrvPluginCfgTotal()                               { return ExtConfig.Total(); }

//int APIENTRY MtSrvTelnet(const ULONG ip, char *buf, const int maxlen)
//{
//	return ExtProcessor.SrvTelnet(ip, buf, maxlen);
//}

void APIENTRY MtSrvTradesAdd(TradeRecord *trade, const UserInfo *user, const ConSymbol *symbol) { ExtProcessor.SrvTradesAdd(trade, user, symbol); }
void APIENTRY MtSrvTradesAddExt(TradeRecord *trade, const UserInfo *user, const ConSymbol *symbol, const int mode) { ExtProcessor.SrvTradesAddExt(trade, user, symbol, mode); }
void APIENTRY MtSrvTradesUpdate(TradeRecord *trade, UserInfo *user, const int mode) { ExtProcessor.SrvTradesUpdate(trade, user, mode); }
void APIENTRY MtSrvTradeRequestApply(RequestInfo *request, const int isdemo) { ExtProcessor.SrvTradeRequestApply(request, isdemo); }
