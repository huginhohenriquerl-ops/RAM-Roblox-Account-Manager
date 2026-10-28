# MultiBloxy — engenharia reversa

Alvo: `MultiBloxy.exe` v1.1.0.0 (SHA-256 `168eedac1f655cd39f33fd8cfa5c971c46b08dda912b78fe330797eade7912c3`),
assembly .NET Framework 4.8, IL-only, x86 (32-bit preferred), 13 tipos / 61 métodos.
Análise estática com REA (`inspect_managed_artifact`, `inspect_managed_members`): metadados, strings `#US`
e âncoras CIL de cada método. Nenhum código do alvo foi executado.

## Como funciona

| Método | Comportamento observado (CIL) |
|---|---|
| `Program..cctor` | `mutexName = "ROBLOX_singletonEvent"`; `appMutexName = "Global\" + name + "_singletonEvent"` |
| `Program.OpenMutex` | `new Mutex(..., mutexName)` → guarda em `Program.mutex`. Em exceção: status "Error creating Mutex" e abre `ShowMutexErrorDialog` |
| `Program.CloseMutex` | `WaitOne()` → `ReleaseMutex()` → `Close()` |
| `Program.PerformAction` | `Fix` = `HandleCloser.CloseAllHandles()` + `OpenMutex()`; `Abort` = `StopRobloxInstances()` + `Sleep(500)` + `OpenMutex()`; `Retry` = `OpenMutex()` |
| `Program.StopRobloxInstances` | `Process.GetProcessesByName("RobloxPlayerBeta")` → `Kill()` em todos |
| Menu "Start New Roblox Instance" | `Process.Start("roblox-player:")` |
| `HandleCloser.CloseAllHandles` | ver abaixo |

### `HandleCloser.CloseAllHandles` (605 bytes de IL)

1. Para cada processo `RobloxPlayerBeta`:
2. `NtQuerySystemInformation(16 /*SystemHandleInformation*/)` com buffer inicial de 64 KiB, realocando enquanto retornar
   `0xC0000004` (STATUS_INFO_LENGTH_MISMATCH).
3. Percorre `SYSTEM_HANDLE_INFORMATION { uint ProcessId; byte ObjectTypeNumber; byte Flags; ushort Handle; uint Object; uint GrantedAccess }`.
4. Se `ProcessId == pid`: `OpenProcess(0x1F0FFF /*PROCESS_ALL_ACCESS*/)`, `DuplicateHandle` para o próprio processo,
   `NtQueryObject(1 /*ObjectNameInformation*/)` com buffer de 4 KiB.
5. Se o nome contém `ROBLOX_singletonEvent` → `DuplicateHandle(proc, handle, IntPtr.Zero, ..., DUPLICATE_CLOSE_SOURCE)`,
   o que fecha o handle **dentro** do Roblox.

O truque: o Roblox cria um *Event* chamado `ROBLOX_singletonEvent` para detectar se já existe outra instância.
Quando o MultiBloxy ocupa esse nome com um *Mutex* (tipo de objeto diferente), o `CreateEvent` do Roblox falha e o
cliente segue como se fosse o único.

## Problemas encontrados → como o R.A.M resolve

| MultiBloxy | R.A.M (`Core/MultiInstance.cs`) |
|---|---|
| Usa a classe 16 (`SystemHandleInformation`): handle de 16 bits e PID lido como `uint` sobre `USHORT pid + USHORT backtrace` — quebra com handles > 0xFFFF ou PIDs altos | Classe 64 (`SystemExtendedHandleInformation`) com `ULONG_PTR` para PID e handle |
| Chama `NtQueryObject` em **todos** os handles do Roblox — pode travar para sempre em pipes síncronos e é lento | Descobre o `ObjectTypeIndex` de Event/Mutant/File criando objetos-sonda no próprio processo; só consulta nomes de handles desses tipos. Fallback: filtro pelas máscaras de acesso conhecidas que travam |
| Varre a tabela de handles do sistema inteira **uma vez por processo** do Roblox | Uma única varredura para todos os processos |
| `OpenProcess(PROCESS_ALL_ACCESS)` | `OpenProcess(PROCESS_DUP_HANDLE)` (mínimo necessário) |
| Só trata `ROBLOX_singletonEvent` | Trata `ROBLOX_singletonEvent` e o legado `ROBLOX_singletonMutex` |
| Se o Roblox já estava aberto, exige diálogo manual | Recuperação automática: fecha os handles e tenta de novo |
| `Contains("ROBLOX_singletonEvent")` | Compara o final exato do caminho do objeto (`\ROBLOX_singletonEvent`) |
| `Process` nunca é liberado | `Dispose` de todos os `Process` |
| Mata processos com `Kill()` direto | `CloseMainWindow()` primeiro, `Kill()` após timeout |
| — | Opcional (experimental): libera a trava exclusiva de `RobloxCookies.dat` |
