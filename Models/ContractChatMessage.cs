namespace DocAnalyzerAI.Models;

public enum ChatSenderRole
{
    User,
    Assistant
}

public class ContractChatMessage
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public ChatSenderRole Role { get; set; } = ChatSenderRole.User;
    public string Content { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public bool IsError { get; set; }
}
