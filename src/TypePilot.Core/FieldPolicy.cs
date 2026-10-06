namespace TypePilot.Core;

public sealed record FieldContext(string Process, bool? IsPassword, bool Writable, bool Focused, bool Supported);

public static class FieldPolicy
{
    private static readonly HashSet<string> Denied = new(StringComparer.OrdinalIgnoreCase)
    {
        "WindowsTerminal", "cmd", "powershell", "pwsh", "Code", "devenv", "rider64", "idea64", "putty", "ssh",
        "VALORANT", "VALORANT-Win64-Shipping", "java", "javaw", "Minecraft.Windows", "Keepass", "KeepassXC", "1Password", "Bitwarden"
    };
    public static bool Allows(FieldContext context, IEnumerable<string> allowed) =>
        context.IsPassword == false && context.Writable && context.Focused && context.Supported &&
        !Denied.Contains(context.Process) && allowed.Contains(context.Process, StringComparer.OrdinalIgnoreCase);
}
