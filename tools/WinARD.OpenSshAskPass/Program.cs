using WinARD.Transport.Ssh;

var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
foreach (var name in new[]
{
    OpenSshAskPassEnvironment.PipeName,
    OpenSshAskPassEnvironment.Challenge,
    OpenSshAskPassEnvironment.TimeoutMilliseconds,
})
{
    var value = Environment.GetEnvironmentVariable(name);
    if (value is not null)
    {
        environment[name] = value;
    }
}

var exitCode = await OpenSshAskPassClient.RunAsync(
    environment,
    Console.OpenStandardOutput(),
    CancellationToken.None).ConfigureAwait(false);
if (exitCode != 0)
{
    await Console.Error.WriteAsync($"ASKPASS_E_{exitCode}").ConfigureAwait(false);
}

return exitCode;
