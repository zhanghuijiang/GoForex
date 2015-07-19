//+------------------------------------------------------------------+
//|                                       MetaTrader WebRegistration |
//|                 Copyright © 2001-2008, MetaQuotes Software Corp. |
//|                                        http://www.metaquotes.net |
//+------------------------------------------------------------------+
#pragma once
//+------------------------------------------------------------------+
//|                                                                  |
//+------------------------------------------------------------------+
class CStringFile
{
private:
	HANDLE            m_file;                 // file handle
	DWORD             m_file_size;            // file size
	BYTE             *m_buffer;               // read buffer
	int               m_buffer_size;          // buffer size
	int               m_buffer_index;         // current parse pointer
	int               m_buffer_readed;        // readed into buffer
	int               m_buffer_line;          // readed lines counter
public:
	CStringFile(const int nBufSize = 65536);
	~CStringFile();
	//----
	bool              Open(LPCTSTR lpFileName, const DWORD dwAccess, const DWORD dwCreationFlags);
	inline void       Close() { if (m_file != INVALID_HANDLE_VALUE) { CloseHandle(m_file); m_file = INVALID_HANDLE_VALUE; } m_file_size = 0; }
	inline DWORD      Size() const { return(m_file_size); }
	int               Read(void  *buffer, const DWORD length);
	int               Write(const void *buffer, const DWORD length);
	void              Reset();
	int               GetNextLine(char *line, const int maxsize);
};
//+------------------------------------------------------------------+
