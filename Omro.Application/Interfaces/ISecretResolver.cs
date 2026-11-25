namespace Omro.Application.Interfaces;

public interface ISecretResolver
{
    string? ResolveSecret(string? secretRef);
}
