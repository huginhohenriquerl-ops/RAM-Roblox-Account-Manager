<p align="center"><img src="Assets/splash.png" width="360" alt="R.A.M"></p>

# R.A.M — Roblox Account Manager

Gerenciador de contas do Roblox com **multi-instância**, no visual do Claude Desktop e com o **Clawd** de mascote.

> [!IMPORTANT]
> **Este é um projeto de fã, independente e não oficial.** Ele **não tem nenhuma relação** com o
> [MultiBloxy](https://github.com/Zgoly/MultiBloxy), com o [Roblox Account Manager](https://github.com/ic3w0lf22/Roblox-Account-Manager)
> (ic3w0lf22), com a **Anthropic** (criadora do Claude e do Clawd) nem com a **Roblox Corporation**. Nenhum deles
> criou, apoia ou mantém este projeto. O visual inspirado no Claude Desktop e o Clawd são uma homenagem de fã;
> todas as marcas pertencem aos seus donos.

## Instalar

Baixe o **`RAM.AccountManager-win-Setup.exe`** da [última release](https://github.com/huginhohenriquerl-ops/RAM-Roblox-Account-Manager/releases/latest)
e execute. Não precisa de administrador; se faltar o .NET 8 Desktop Runtime, o instalador baixa sozinho.

O R.A.M se **atualiza sozinho**: ao abrir (e a cada 4 horas) ele procura uma release nova aqui no GitHub, baixa em
segundo plano e mostra "Versão X pronta · Reiniciar".

## Recursos

- **Multi-instância** — várias janelas do Roblox ao mesmo tempo. Técnica estudada por engenharia reversa do
  MultiBloxy e reimplementada com melhorias; detalhes em [docs/MultiBloxy-RE.md](docs/MultiBloxy-RE.md).
  Funciona enquanto o R.A.M estiver aberto (ele fica na bandeja).
- **Sessões isoladas** — trava o `RobloxCookies.dat` enquanto a multi-instância está ativa, para que cada janela guarde
  só o próprio login (evita o erro 773 nos teleportes e uma conta mostrando o histórico de outra).
- **Conta certa em cada janela** — cada conta tem um ID fixo passado ao Roblox; o R.A.M lê a linha de comando de cada
  cliente para saber de quem ele é, e fecha a sessão antiga quando você abre a mesma conta de novo.
  Ideias estudadas no Roblox Account Manager e reimplementadas do zero; veja [docs/RobloxAccountManager-RE.md](docs/RobloxAccountManager-RE.md).
- **Nova instância do Roblox** — abre outra janela sem conta salva; o login é feito na tela do próprio Roblox.
- **Contas salvas** — cada conta é validada no Roblox e guardada em `%LOCALAPPDATA%\RAM\accounts.dat`,
  criptografada com DPAPI (só o seu usuário do Windows consegue ler).
- **Abrir em jogos** — selecione uma ou mais contas (Ctrl/Shift + clique), cole um Place ID, link de jogo, link de
  servidor privado, um link de convite (`roblox.com/share`) ou um Job ID e aperte Enter (com a caixa vazia, abre o Roblox na tela inicial). Mostra a prévia do jogo e espera um intervalo entre as contas.
- **Instâncias** — lista os `RobloxPlayerBeta` abertos com a conta de cada um, renomeia as janelas, foca ou fecha.
- Tema claro/escuro, ícone na bandeja e o Clawd reagindo a tudo.

## Desenvolvimento

Requer o .NET 8 SDK.

```bash
dotnet build -c Release
```

- `tools/pack.ps1 -Version 1.2.3` gera o instalador localmente em `Releases/` (precisa do `vpk`: `dotnet tool install -g vpk`).
- `tools/make-icon.ps1` regenera `Assets/ram.ico` e `Assets/splash.png` a partir do sprite do Clawd.

### Publicar uma versão

Atualize `<Version>` em `RAM.csproj`, faça commit e envie uma tag:

```bash
git tag v1.0.1
git push origin v1.0.1
```

O workflow [`release.yml`](.github/workflows/release.yml) compila, gera o instalador (com pacotes delta) e publica a release.
As cópias instaladas recebem a atualização automaticamente.

## Aviso

Projeto de fã, não oficial e sem vínculo com MultiBloxy, Roblox Account Manager, Anthropic ou Roblox Corporation.
Nenhum código desses projetos foi copiado: as técnicas foram estudadas e reimplementadas.
Use por sua conta e risco e respeite os Termos de Uso do Roblox. Nunca compartilhe o cookie `.ROBLOSECURITY` de uma conta com ninguém.
