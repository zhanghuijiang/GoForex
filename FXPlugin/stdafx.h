// stdafx.h : include file for standard system include files,
// or project specific include files that are used frequently, but
// are changed infrequently
//

#pragma once

#include "targetver.h"

#define WIN32_LEAN_AND_MEAN             // Exclude rarely-used stuff from Windows headers
// Windows Header Files:
#include <windows.h>
#include <time.h>
#include <stdio.h>
#include <stdlib.h>
#include <process.h>
#include <math.h>
#include <io.h>
#include <sys/stat.h>
#include <winsock2.h>

// TODO: reference additional headers your program requires here

//--- Server API
#include "MT4ServerAPI.h"
#include "Configuration.h"
#include "StringFile.h"
#include "Common.h"

//---- Macros for strings
#define TERMINATE_STR(str)  str[sizeof(str)-1]=0;
#define COPY_STR(dst,src) { strncpy(dst,src,sizeof(dst)-1); dst[sizeof(dst)-1]=0; }
