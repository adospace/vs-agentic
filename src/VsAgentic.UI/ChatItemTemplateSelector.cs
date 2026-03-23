using System.Windows;
using System.Windows.Controls;
using VsAgentic.UI.ViewModels;

namespace VsAgentic.UI;

public class ChatItemTemplateSelector : DataTemplateSelector
{
    public DataTemplate? UserTemplate { get; set; }
    public DataTemplate? AssistantTemplate { get; set; }
    public DataTemplate? ToolStepTemplate { get; set; }
    public DataTemplate? ThinkingTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
    {
        return item switch
        {
            ChatItemViewModel { Type: ChatItemType.User } => UserTemplate,
            ChatItemViewModel { Type: ChatItemType.Assistant } => AssistantTemplate,
            ChatItemViewModel { Type: ChatItemType.ToolStep } => ToolStepTemplate,
            ChatItemViewModel { Type: ChatItemType.Thinking } => ThinkingTemplate,
            _ => base.SelectTemplate(item, container)
        };
    }
}
