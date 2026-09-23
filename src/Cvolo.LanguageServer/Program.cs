using Cvolo.LanguageServer.Cli;

var rootCommand = new CvoloLanguageServerRootCommand();
return await rootCommand.Parse(args).InvokeAsync().ConfigureAwait(false);
