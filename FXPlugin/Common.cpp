//+------------------------------------------------------------------+
//|                                       MetaTrader WebRegistration |
//|                 Copyright © 2001-2008, MetaQuotes Software Corp. |
//|                                        http://www.metaquotes.net |
//+------------------------------------------------------------------+
#include "stdafx.h"
//+------------------------------------------------------------------+
//| Reading number parameter                                         |
//+------------------------------------------------------------------+
int GetIntParam(LPCSTR string, LPCSTR param, int *data)
{
	//---- checks
	if (string == NULL || param == NULL || data == NULL) return(FALSE);
	//---- find
	if ((string = strstr(string, param)) == NULL)       return(FALSE);
	//---- all right
	*data = atoi(&string[strlen(param)]);
	return(TRUE);
}
//+------------------------------------------------------------------+
//| Reading floating parameter                                       |
//+------------------------------------------------------------------+
int GetFltParam(LPCSTR string, LPCSTR param, double *data)
{
	//---- checks
	if (string == NULL || param == NULL || data == NULL) return(FALSE);
	//---- find
	if ((string = strstr(string, param)) == NULL)       return(FALSE);
	//---- all right
	*data = atof(&string[strlen(param)]);
	return(TRUE);
}
//+------------------------------------------------------------------+
//| Reading string parameter                                         |
//+------------------------------------------------------------------+
int GetStrParam(LPCSTR string, LPCSTR param, char *buf, const int maxlen)
{
	int i = 0;
	//---- checks
	if (string == NULL || param == NULL || buf == NULL)  return(FALSE);
	//---- find
	if ((string = strstr(string, param)) == NULL)       return(FALSE);
	//---- receive result
	string += strlen(param);
	while (*string != 0 && *string != '|' && i<maxlen) { *buf++ = *string++; i++; }
	*buf = 0;
	//----
	return(TRUE);
}
//+------------------------------------------------------------------+
//| Check complexity of password                                     |
//+------------------------------------------------------------------+
int CheckPassword(LPCSTR password)
{
	char   tmp[256];
	int    len, num = 0, upper = 0, lower = 0;
	USHORT type[256];
	//----
	if (password == NULL) return(FALSE);
	//---- check len
	if ((len = strlen(password))<5) return(FALSE);
	//---- must Upper case,lower case and digits
	strcpy(tmp, password);
	if (GetStringTypeA(LOCALE_SYSTEM_DEFAULT, CT_CTYPE1, tmp, len, (USHORT*)type))
	{
		for (int i = 0; i<len; i++)
		{
			if (type[i] & C1_DIGIT)  { num = 1;   continue; }
			if (type[i] & C1_UPPER)  { upper = 1; continue; }
			if (type[i] & C1_LOWER)  { lower = 1; continue; }
			if (!(type[i] & (C1_ALPHA | C1_DIGIT))) { num = 2; break; }
		}
	}
	//---- compute complexity
	return((num + upper + lower) >= 2);
}
//+------------------------------------------------------------------+
//|                                                                  |
//+------------------------------------------------------------------+
char* insert(void *base, const void *elem, size_t num, const size_t width, int(__cdecl *compare)(const void *elem1, const void *elem2))
{
	//---- проверки
	if (base == NULL || elem == NULL || compare == NULL) return(NULL);
	//---- первый элемент?
	if (num<1) { memcpy(base, elem, width); return(char*)(base); }
	//---- 
	register char *lo = (char *)base;
	register char *hi = (char *)base + (num - 1) * width, *end = hi;
	register char *mid;
	unsigned int   half;
	int            result;
	//----
	while (num>0)
	{
		half = num / 2;
		mid = lo + half*width;
		//---- compare
		if ((result = compare(elem, mid))>0) // data[mid]<elem
		{
			lo = mid + width;
			num = num - half - 1;
		}
		else if (result<0)                // data[mid]>elem
		{
			num = half;
		}
		else                             // data[mid]==elem
			return(NULL);
	}
	//---- вставляем
	memmove(lo + width, lo, end - lo + width);
	memcpy(lo, elem, width);
	//----
	return(lo);
}
//+------------------------------------------------------------------+
//| Constants                                                        |
//+------------------------------------------------------------------+
static const double ExtDecimalArray[9] = { 1.0, 10.0, 100.0, 1000.0, 10000.0, 100000.0, 1000000.0, 10000000.0, 100000000.0 };
static LPCSTR       ExtOperations[9] = { "buy", "sell", "buy limit", "sell limit", "buy stop", "sell stop", "balance", "credit", "error" };
//+------------------------------------------------------------------+
//| Normalize Double                                                 |
//+------------------------------------------------------------------+
double __fastcall NormalizeDouble(const double val, int digits)
{
	if (digits<0) digits = 0;
	if (digits>8) digits = 8;
	//----
	const double p = ExtDecimalArray[digits];
	return((val >= 0.0) ? (double(__int64(val*p + 0.5000001)) / p) : (double(__int64(val*p - 0.5000001)) / p));
}
//+------------------------------------------------------------------+
//| Dec Pow                                                          |
//+------------------------------------------------------------------+
double DecPow(const int digits)
{
	//---- check
	if (digits<0 || digits>8) return(0);
	//---- return result
	return ExtDecimalArray[digits];
}
//+------------------------------------------------------------------+
//| Trade command description                                        |
//+------------------------------------------------------------------+
LPCSTR GetCmd(const int cmd)
{
	//---- check
	if (cmd<OP_BUY || cmd>OP_CREDIT) return(ExtOperations[8]);
	//----
	return(ExtOperations[cmd]);
}
//+------------------------------------------------------------------+
//|                                                                  |
//+------------------------------------------------------------------+
void ClearLF(char *line)
{
	//---
	if (line == NULL) return;
	while (*line>31 || *line<0 || *line == 9 || *line == 27) line++;
	*line = 0;
	//---
}
