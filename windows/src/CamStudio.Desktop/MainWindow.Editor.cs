using System.Windows;
using CamStudio.Desktop.Media;
using CamStudio.Editor;

namespace CamStudio.Desktop;

/// <summary>Hosts the video editor (CamStudio.Editor) on the Editor page. Created on first visit.</summary>
public partial class MainWindow
{
    private EditorView? _editor;

    private EditorView EnsureEditor()
    {
        if (_editor != null)
            return _editor;
        EditorView.RecordingsFolder = VideoRecorder.RecordingsDirectory;
        _editor = new EditorView();
        EditorHost.Content = _editor;
        return _editor;
    }

    private void OpenInEditor(string path) => EnsureEditor().ImportFiles(new[] { path }, addToTimeline: true);

    /// <summary>Asks about unsaved edits before quitting. False = the user cancelled.</summary>
    public bool ConfirmEditorClose()
    {
        if (_editor?.HasUnsavedChanges != true)
            return true;
        ((App)Application.Current).ShowMain();
        NavEditor.IsChecked = true;
        return _editor.ConfirmDiscardChanges();
    }
}
