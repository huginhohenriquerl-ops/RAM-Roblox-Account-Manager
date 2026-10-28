# Roblox Account Manager 3.7.2 — engenharia reversa

Alvo: `Roblox Account Manager.exe` 3.7.2 (SHA-256 `be9ddcdedf8c36c64e6b0a32d2686b74a112913c54217ccaa46675bfd1dc82f1`),
.NET Framework 4.8, 283 tipos / 1480 métodos. Análise estática com REA (metadados, strings `#US` e âncoras CIL de
`Account`, `Program`, `RobloxProcess`, `RobloxWatcher`, `Presence`, `ClientSettingsPatcher` e das máquinas de estado
`<JoinServer>d__53` / `<LaunchAccounts>d__155`). Nenhum código do alvo foi executado e nenhum código dele foi copiado:
o R.A.M reimplementa as ideias do zero. Os arquivos de dados do usuário (contas, logs) não foram lidos.

## O que ele faz e o que o R.A.M adotou

| Roblox Account Manager | Evidência | No R.A.M 1.1 |
|---|---|---|
| **"773 error fix"**: em `Program.Main`, abre `%LOCALAPPDATA%\Roblox\LocalStorage\RobloxCookies.dat` com um `FileStream` e o mantém aberto até o app fechar (desligável criando `no773fix.txt`). | strings `Roblox\LocalStorage\RobloxCookies.dat`, `no773fix.txt`, `newobj FileStream` guardado num local até o fim de `Main` | `SessionIsolation`: trava o arquivo com `FileShare.None` enquanto a multi-instância está ativa. Se um cliente já tem o arquivo aberto, só esse handle é liberado e a trava é tentada de novo. Opção "Isolar as sessões das contas". |
| **BrowserTrackerID fixo por conta**, e o processo é associado à conta lendo `-b (\d+)` da linha de comando do `RobloxPlayerBeta`. | regex `\-b (\d+)` em `JoinServer`, `GetProcessesByName("RobloxPlayerBeta")` | `Account.BrowserTrackerId` + `Native.GetCommandLine` (via `NtQueryInformationProcess`, sem WMI). O mapeamento por tempo continua só como reserva. |
| **AutoCloseLastProcess**: antes de abrir uma conta, fecha os clientes dela já abertos (`CloseMainWindow`, espera, `Kill`). | string `AutoCloseLastProcess`, chamadas `CloseMainWindow`/`Task.Delay`/`Kill` | Opção "Fechar a sessão anterior da mesma conta" (ligada por padrão). |
| Links `roblox.com/share`: resolve `Server` (`privateServerInviteData`) **e** `ExperienceInvite` (`experienceInviteData` → `placeId` + `instanceId`). | strings `sharelinks/v1/resolve-link`, `experienceInviteData`, `instanceId` | `ResolveShareLinkAsync` aceita os dois tipos. |
| Espera configurável entre contas (`AccountJoinDelayMS`). | string em `LaunchAccounts` | Já existia ("Intervalo entre contas"). |
| Multi-Roblox fechando handles com o `handle.exe` da Sysinternals (`handle.bin`, EULA no registro). | strings `SOFTWARE\Sysinternals\Handle`, `handle.bin` | Não adotado: o R.A.M já fecha os handles nativamente (ver `docs/MultiBloxy-RE.md`), sem baixar ferramentas externas. |
| Watcher que lê os logs do Roblox (DataModel, disconnect) e mata clientes por pouca memória/título errado. | regexes `FLog::Output`, `Sending disconnect`, `Low Memory` | Não adotado nesta versão (depende de formatos de log que mudam com frequência). |
| Patch de `ClientAppSettings.json` (FPS). | `DFIntTaskSchedulerTargetFps` | Não adotado: conflita com bootstrappers e com a allowlist de FFlags do Roblox. |

## Melhorias próprias desta versão

- Requisições ao Roblox repetem automaticamente em 429/5xx, respeitando `Retry-After`.
- O processo de fundo da bandeja do Roblox (`--launch-to-tray`) não aparece mais como "instância".
- Se outro programa (outro R.A.M, MultiBloxy, um bootstrapper) já segura `ROBLOX_singletonEvent`, a multi-instância é
  mostrada como ativa em vez de erro: o Roblox não consegue criar o próprio Event de qualquer jeito.
