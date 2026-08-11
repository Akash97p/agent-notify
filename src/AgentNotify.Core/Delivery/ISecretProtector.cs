namespace AgentNotify.Core.Delivery;

public interface ISecretProtector
{
    string Protect(string plaintext);
    string Unprotect(string envelope);
}
