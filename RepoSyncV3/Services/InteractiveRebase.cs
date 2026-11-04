using System.IO;
using System.Text;

namespace RepoSyncV3.Services;

/// <summary>One line of the rebase todo. Message is editable for reword.</summary>
public sealed class RebaseStep
{
    public required string Hash { get; init; }
    public string ShortHash => Hash[..Math.Min(8, Hash.Length)];
    public required string OriginalMessage { get; init; }
    public string Message { get; set; } = string.Empty;
    public string Action { get; set; } = "pick";
}

/// <summary>Turns a plan into a git rebase todo; reword becomes pick + "exec git commit --amend -F file" so
/// no editor is ever opened.</summary>
public static class InteractiveRebase
{
    /// <returns>Todo text; message files for rewords are written into tempDir.</returns>
    public static string BuildTodo(IReadOnlyList<RebaseStep> steps, string tempDir)
    {
        var sb = new StringBuilder();
        var n = 0;
        foreach (var s in steps)
        {
            var reword = s.Action == "reword" || (s.Action == "pick" && s.Message.Trim() != s.OriginalMessage.Trim() && s.Message.Trim().Length > 0);
            var verb = s.Action == "reword" ? "pick" : s.Action;
            sb.Append(verb).Append(' ').Append(s.Hash).Append('\n');
            if (reword && s.Message.Trim().Length > 0)
            {
                var file = Path.Combine(tempDir, $"msg{n++}.txt");
                File.WriteAllText(file, s.Message.Trim() + "\n", new UTF8Encoding(false));
                sb.Append("exec git commit --amend --no-verify -F '").Append(file.Replace('\\', '/')).Append("'\n");
            }
        }
        return sb.ToString();
    }
}
