// ==WindhawkMod==
// @id              pdnclassic-loader
// @name            PDNClassic Loader
// @description     Loader for the PDNClassic mod
// @version         1.0.0
// @author          aubymori
// @github          https://github.com/aubymori
// @include         paintdotnet.exe
// @license         GPL-3.0
// ==/WindhawkMod==

// ==WindhawkModReadme==
/*
# PDNClassic Loader
This is the loader mod that loads the [PDNClassic](https://github.com/aubymori/PDNClassic) mod
into [Paint.NET](https://paint.net/).
*/
// ==/WindhawkModReadme==

// ==WindhawkModSettings==
/*
- dll_path: C:\path\to\PDNClassic.dll
  $name: Path to PDNClassic.dll
  $description: Provide the absolute path to PDNClassic.dll here.
*/
// ==/WindhawkModSettings==

#include <windhawk_utils.h>

void PrependToEnvVar(LPCWSTR pszVar, LPCWSTR pszPrepend)
{
    DWORD dwLength = GetEnvironmentVariableW(pszVar, nullptr, 0);

    /* If we have no startup hooks already, set it to our path. */
    if (dwLength == 0)
    {
        Wh_Log(L"Setting %s to '%s'", pszVar, pszPrepend);
        SetEnvironmentVariableW(pszVar, pszPrepend);
        return;
    }

    LPWSTR szEnv = new WCHAR[dwLength];
    GetEnvironmentVariableW(pszVar, szEnv, dwLength);

    LPWSTR szEnvSet = new WCHAR[dwLength + wcslen(pszPrepend) + 1];
    wcscpy(szEnvSet, pszPrepend);
    wcscat(szEnvSet, L";");
    wcscat(szEnvSet, szEnv);

    Wh_Log(L"Setting %s to '%s'", pszVar, szEnvSet);

    SetEnvironmentVariableW(pszVar, szEnvSet);

    delete[] szEnv;
    delete[] szEnvSet;
}

BOOL Wh_ModInit(void)
{
    auto path = WindhawkUtils::StringSetting::make(L"dll_path");
    PrependToEnvVar(L"DOTNET_STARTUP_HOOKS", path.get());
    return TRUE;
}