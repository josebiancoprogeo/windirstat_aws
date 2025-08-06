# windirstat_aws
Gerador de estatísticas do uso do S3 da Amazon inspirado no
[WinDirStat](https://windirstat.net/).

## Funcionalidades previstas

* Mapear a estrutura de diretórios de um bucket S3 e calcular o tamanho
  ocupado por cada pasta e objeto.
* Apresentar os resultados em um visualizador semelhante ao WinDirStat,
  com árvore hierárquica e treemap para facilitar a identificação dos
  maiores consumidores de espaço.
* Exibir estatísticas de tipos de arquivos e permitir ordenação ou
  filtragem por extensão, tamanho ou caminho.
* Suportar múltiplos buckets e perfis de credenciais AWS através da
  configuração padrão do SDK.
* Oferecer opções para ignorar prefixos/pastas e exportar relatórios em
  formatos como CSV ou JSON.
* Mostrar progresso da varredura e permitir a interrupção da análise a
  qualquer momento.
* Disponibilizar uma interface de linha de comando para execução
  automatizada e integração com scripts.

Estas funcionalidades descrevem o escopo inicial da ferramenta e podem
ser expandidas conforme a necessidade do projeto.

## Configuração do S3

Para gerar o arquivo de configuração com as credenciais do S3 de forma
criptografada, execute:

```bash
dotnet run --project s3_config_cli
```

O utilitário solicitará as chaves de acesso, região, bucket e uma senha
para proteger os dados. O resultado será salvo no arquivo
`s3config.enc` no diretório atual.
