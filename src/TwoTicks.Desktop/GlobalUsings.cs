// Uno has no RichTextBlock. Its TextBlock holds the same runs and links, and
// the emoji are a font here, so the app's rich text is a TextBlock; see the
// project file for the same change in XAML.
global using RichTextBlock = Microsoft.UI.Xaml.Controls.TextBlock;
