# YakUI 0.1.0

Windows app to browse, install and manage Rhino Yak packages (Rhino 7, 8, 9).

## Build

Requirements: .NET 8 SDK, Inno Setup 6 (for the EXE installer).

```
powershell -ExecutionPolicy Bypass -File build\publish.ps1
```

Output in `artifacts\`: `YakUI-<version>-win-<arch>-setup.exe`, `YakUI-<version>-win-<arch>-portable.zip`, `SHA256SUMS.txt`.

# YakUI — структура папок при установке

`%LOCALAPPDATA%` = `C:\Users\<вы>\AppData\Local`, `%APPDATA%` = `C:\Users\<вы>\AppData\Roaming`.

## 1. Сама программа

```
%LOCALAPPDATA%\Programs\YakUI\      ← папку можно сменить при установке
├─ YakUI.exe                        ← окно программы
├─ YakUI.Cli.exe                    ← консольная версия (команды list, install…)
├─ *.dll                            ← библиотеки .NET и Avalonia (их много, это нормально)
└─ unins000.exe                     ← удаление программы
```

- Ставится **только для текущего пользователя**, без прав администратора.
- Ярлык в меню «Пуск», на рабочем столе — по галочке.
- Запись в «Параметры → Приложения», оттуда же удаление.

## 2. Настройки (появляются при первом запуске)

```
%APPDATA%\YakUI\
└─ settings.json                    ← тема, язык, папки, прокси, hosts-строки и т.д.
```

## 3. Рабочие данные (появляются по ходу работы)

```
%LOCALAPPDATA%\YakUI\
├─ yakui.db                         ← база: кэш каталога, журнал, закрепления, реестр установок
├─ icons\                           ← иконки пакетов (кэш)
├─ downloads\                       ← скачанные .yak (папку можно сменить в настройках)
├─ yak\                             ← скачанные версии yak.exe
│   └─ .quarantine\                 ← сборки yak, не прошедшие проверку
├─ logs\                            ← технические логи, включая db-open.log
└─ backups\hosts\                   ← копия hosts на время сеанса
```

## 4. Куда YakUI пишет только по команде пользователя

Это уже не файлы YakUI, а установленные им плагины:

| Действие | Куда |
|---|---|
| Стандартная установка | `%APPDATA%\McNeel\Rhinoceros\packages\<7.0 / 8.0>\<Плагин>\<версия>\` |
| Установка в свою папку | `<ваша папка>\<Плагин>\<версия>\` + ссылка `%APPDATA%\Grasshopper\Libraries\<Плагин>.ghlink` |
| `.ghuser` при установке в свою папку | `%APPDATA%\Grasshopper\UserObjects\<Плагин>\` |
| Кнопка hosts | `C:\Windows\System32\drivers\etc\hosts` (с UAC), при закрытии YakUI возвращается как было |

## 5. При удалении YakUI

- Папка программы удаляется полностью.
- Установщик **спрашивает**, удалить ли пункты 2 и 3 (настройки, кэш, скачанные файлы, журнал).
- **Установленные плагины Rhino не трогаются** — они остаются и работают дальше.

## ZIP-версия

Устроена так же. Отличие одно: если рядом с `YakUI.exe` лежит файл `portable.flag`, пункты 2 и 3 хранятся не в профиле Windows, а в папке `data\` рядом с программой.
