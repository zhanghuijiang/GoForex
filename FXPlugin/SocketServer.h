//+------------------------------------------------------------------+
//|                                                  Server Emulator |
//|                   Copyright 2001-2014, MetaQuotes Software Corp. |
//|                                        http://www.metaquotes.net |
//+------------------------------------------------------------------+
#pragma once

#include "socketclient.h"
//+------------------------------------------------------------------+
//|                                                                  |
//+------------------------------------------------------------------+
class CSocketServer : public CSocketClient
{
private:
	ServerCfg         m_cfg;                   // server configuration
	CSocketClient    *m_connections;           // list of connections
	CProcessor&       m_Processor;

public:
	CSocketServer(CProcessor& processor);
	virtual          ~CSocketServer();

	void              Initialize(void);
	int               StartServer(const int port);
	virtual void      Shutdown(void);
	void SendAll(STradeAdd* tradesAdd);

protected:
	virtual void      ThreadProcess(void);
};
//+------------------------------------------------------------------+
