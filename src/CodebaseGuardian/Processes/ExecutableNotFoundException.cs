namespace CodebaseGuardian.Processes;

public sealed class ExecutableNotFoundException(string fileName, Exception inner)
    : Exception($"Executable '{fileName}' could not be started; is it installed and on PATH?", inner)
{
    public string FileName { get; } = fileName;
}
