# EpriceUtil

API interna do Eprice para os três trabalhos pesados que saíram do EpriceCore: screenshot com Chromium, giro de imagem e remoção de fundo. O Core continua decidindo quando executar cada um e gravando o arquivo no storage. Esta API só devolve os bytes.

Roda em .NET 10, no endereço local `http://localhost:5090`.

## O que ela faz

| Rota | Entrada | Saída |
|---|---|---|
| `POST /api/Capture` | JSON | JPEG ou PNG |
| `POST /api/Rotate` | arquivo no campo `file` | imagem girada 90 graus |
| `POST /api/RemoveBackground` | arquivo no campo `file` | PNG sem fundo |
| `GET /` | — | versão, IP do cliente e IP do servidor |
| `GET /health` | — | `Healthy` |

Todas as rotas `POST` exigem o header `X-Util-Token`. A raiz e o `/health` não exigem.

O corpo de cada requisição pode ter até 100 MB.

### Captura

```http
POST /api/Capture
X-Util-Token: <mesmo valor de UtilToken>
Content-Type: application/json

{
  "url": "https://exemplo",
  "width": 1920,
  "height": 1080,
  "awaitSignal": "VIEW_READY",
  "type": "jpeg"
}
```

`type` aceita `jpeg` ou `png`. Sem largura e altura, a captura é da página inteira. `awaitSignal` espera `localStorage[sinal] === "true"`, o mesmo sinal `VIEW_READY` usado nas telas do Core. A captura abre o Chromium, tira o screenshot e fecha o navegador.

### Giro

```http
POST /api/Rotate
X-Util-Token: <mesmo valor de UtilToken>
Content-Type: multipart/form-data

file: <arquivo>
```

A extensão do arquivo escolhe o tratamento. `.gif` gira cada frame no ImageMagick, com no máximo 256 MB de RAM e o restante em disco. Os demais formatos giram no ImageSharp e voltam no formato original.

### Remoção de fundo

```http
POST /api/RemoveBackground
X-Util-Token: <mesmo valor de UtilToken>
Content-Type: multipart/form-data

file: <arquivo>
```

Usa o modelo U2Net. A sessão entra na memória só durante o recorte e é descartada ao terminar. Na primeira chamada o modelo é baixado de `U2NetUrl` para `%LOCALAPPDATA%\eprice-util\u2net.onnx`.

## Concorrência

`JobGateLimit` define quantos trabalhos pesados rodam ao mesmo tempo. O padrão é `1`. Captura, giro e remoção de fundo compartilham essa trava: a chamada seguinte espera a anterior terminar.

Para aceitar dois ao mesmo tempo, defina `JobGateLimit` como `2` e reinicie o processo. As duas chamadas passam a dividir a memória da máquina.

## Configuração

| Chave | Uso |
|---|---|
| `UtilToken` | Token comparado com o header `X-Util-Token` |
| `JobGateLimit` | Quantidade de trabalhos simultâneos. Ausente ou inválido vale `1` |
| `U2NetUrl` | Endereço do `u2net.onnx` |
| `UtilVersion` | Versão exibida em `GET /` |

No Core, a API e o worker precisam das chaves `UtilBaseUrl` e `UtilToken`. O token tem de ser o mesmo dos dois lados. Sem essas chaves, o Core não executa captura, giro nem remoção de fundo.

Publique o EpriceUtil em outra máquina ou em outro plano. No plano da API, os dois processos dividem a mesma memória.

## Como rodar

```bash
dotnet run --project EpriceUtil.Api
```

A primeira captura instala o Chromium do Playwright, se ele ainda não estiver na máquina. O `/health` responde antes dessa instalação.

## Projeto

```
EpriceUtil.Api
  Controllers        rotas
  Services           captura, giro e remoção de fundo
  JobGate.cs         limite de trabalhos simultâneos
  UtilTokenMiddleware.cs
  PlaywrightInstaller.cs
```

A solução é `EpriceUtil.slnx`.
