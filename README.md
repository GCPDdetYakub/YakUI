# YakUI 0.1.0

Windows app to browse, install and manage Rhino Yak packages (Rhino 7, 8, 9).

## Build

Requirements: .NET 8 SDK, Inno Setup 6 (for the EXE installer).

```
powershell -ExecutionPolicy Bypass -File build\publish.ps1
```

Output in `artifacts\`: `YakUI-<version>-win-<arch>-setup.exe`, `YakUI-<version>-win-<arch>-portable.zip`, `SHA256SUMS.txt`.

1. Сама программа
%LOCALAPPDATA%\Programs\YakUI\      ← папку можно сменить при установке
├─ YakUI.exe                        ← окно программы
├─ YakUI.Cli.exe                    ← консольная версия (команды list, install…)
├─ *.dll                            ← библиотеки .NET и Avalonia (их много, это нормально)
└─ unins000.exe                     ← удаление программы
Ставится только для вас, без прав администратора.
Ярлык в меню «Пуск», на рабочем столе — по галочке.
Запись в «Параметры → Приложения», оттуда же удаление.

2. Настройки (появляются при первом запуске)
%APPDATA%\YakUI\
└─ settings.json                    ← тема, язык, папки, прокси, hosts-строки и т.д.

3. Рабочие данные (появляются по ходу работы)
%LOCALAPPDATA%\YakUI\
├─ yakui.db                         ← база: кэш каталога, журнал, закрепления, реестр установок
├─ icons\                           ← иконки пакетов (кэш)
├─ downloads\                       ← скачанные .yak (папку можно сменить в настройках)
├─ yak\                             ← скачанные версии yak.exe
│   └─ .quarantine\                 ← сборки yak, не прошедшие проверку
├─ logs\                            ← технические логи, включая db-open.log
└─ backups\hosts\                   ← копия hosts на время сеанса
