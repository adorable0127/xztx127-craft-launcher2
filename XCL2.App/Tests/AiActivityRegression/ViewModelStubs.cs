// Only the platform shell is stubbed. AiActivityVm and its Details projection are the production source.
namespace System.Windows
{
    public enum Visibility { Visible, Hidden, Collapsed }
}

namespace XCL2.App.Views
{
    public partial class BubbleVm
    {
        public bool IsGenerating { get; set; }
        public bool IsAssistantMessage { get; set; }
        public XCL2.App.Models.AiChatMessage? OriginalMessage { get; set; }
        private void Raise(string propertyName) { }
    }
}
