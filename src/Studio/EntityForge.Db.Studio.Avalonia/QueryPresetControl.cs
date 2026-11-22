using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using EntityForge.Db.Studio.Queries;

namespace EntityForge.Db.Studio.Avalonia;

internal sealed class QueryPresetControl : UserControl
{
    public QueryPresetControl(StudioHostServices host, Func<string, bool, QueryPreset> capture, Action<QueryPreset> load)
    {
        var button = new Button { Content = "Presets", HorizontalAlignment = HorizontalAlignment.Left };
        Content = button;
        button.Click += async (_, _) =>
        {
            var panel = new StackPanel { Spacing = 10, Width = 360, Margin = new Thickness(4) };
            var flyout = new Flyout { Content = panel };
            var choices = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "Choose a saved query" };
            var name = new TextBox { PlaceholderText = "Preset name", MaxLength = 120 };
            var variables = new CheckBox { Content = "Include GraphQL variable values", IsChecked = false };
            var confirm = new CheckBox { Content = "Confirm replacing or deleting selected preset" };
            var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
            var actions = new WrapPanel { Orientation = Orientation.Horizontal };
            QueryPreset[] items = [];
            QueryPreset Selected() => choices.SelectedIndex >= 0 && choices.SelectedIndex < items.Length
                ? items[choices.SelectedIndex] : throw new InvalidOperationException("Choose a saved query first.");
            async Task Refresh()
            {
                string? id = choices.SelectedIndex >= 0 ? Selected().Id : null;
                items = (await host.QueryPresets.ReadAsync()).Presets.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToArray();
                choices.ItemsSource = items.Select(p => $"{p.Name} · {p.Kind}").ToArray();
                choices.SelectedIndex = Array.FindIndex(items, p => p.Id == id);
            }
            choices.SelectionChanged += (_, _) =>
            {
                if (choices.SelectedIndex >= 0)
                {
                    name.Text = Selected().Name;
                }
                confirm.IsChecked = false;
            };
            void Add(string title, Func<Task> action)
            {
                var actionButton = new Button { Content = title, Margin = new Thickness(0, 0, 6, 6) };
                actions.Children.Add(actionButton);
                actionButton.Click += async (_, _) =>
                {
                    actions.IsEnabled = false;
                    try
                    {
                        await action();
                    }
                    catch (Exception ex)
                    {
                        status.Text = ex.Message;
                    }
                    finally
                    {
                        actions.IsEnabled = true;
                    }
                };
            }
            void Confirm()
            {
                if (confirm.IsChecked != true)
                {
                    throw new InvalidOperationException("Tick the confirmation box before replacing or deleting a preset.");
                }
            }
            Add("Load", () =>
            {
                load(Selected());
                flyout.Hide();
                return Task.CompletedTask;
            });
            Add("Save new", async () =>
            {
                await host.QueryPresets.SaveAsync(capture(name.Text ?? "", variables.IsChecked == true));
                await Refresh();
                status.Text = "Saved locally.";
            });
            Add("Replace", async () =>
            {
                Confirm();
                var selected = Selected();
                await host.QueryPresets.SaveAsync(capture(name.Text ?? "", variables.IsChecked == true) with
                {
                    Id = selected.Id
                }, true);
                await Refresh();
                confirm.IsChecked = false;
                status.Text = "Preset replaced.";
            });
            Add("Rename", async () =>
            {
                await host.QueryPresets.RenameAsync(Selected().Id, name.Text ?? "");
                await Refresh();
                status.Text = "Preset renamed.";
            });
            Add("Delete", async () =>
            {
                Confirm();
                await host.QueryPresets.DeleteAsync(Selected().Id);
                await Refresh();
                confirm.IsChecked = false;
                status.Text = "Preset deleted.";
            });
            Add("Export all…", async () =>
            {
                string json = QueryPresetFormat.Serialize(await host.QueryPresets.ReadAsync());
                if (host.ExportQueryPresets is { } export)
                {
                    await export(json);
                }
                else
                {
                    var provider = TopLevel.GetTopLevel(this)?.StorageProvider ?? throw new InvalidOperationException("File saving is unavailable.");
                    var file = await provider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = "Export query presets", SuggestedFileName = "entityforge-query-presets.json", DefaultExtension = "json", FileTypeChoices = [JsonFiles] });
                    if (file is null)
                    {
                        return;
                    }

                    using (file)
                    {
                        await using var stream = await file.OpenWriteAsync();
                        stream.SetLength(0);
                        await using var writer = new StreamWriter(stream);
                        await writer.WriteAsync(json);
                    }
                }
                status.Text = "Export requested. Query text and saved variable values are included.";
            });
            Add("Import…", async () =>
            {
                string? json;
                if (host.ImportQueryPresets is { } import)
                {
                    json = await import();
                }
                else
                {
                    var provider = TopLevel.GetTopLevel(this)?.StorageProvider ?? throw new InvalidOperationException("File opening is unavailable.");
                    var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Import query presets", AllowMultiple = false, FileTypeFilter = [JsonFiles] });
                    if (files.Count == 0)
                    {
                        return;
                    }

                    using var file = files[0];
                    await using var stream = await file.OpenReadAsync();
                    using var buffer = new MemoryStream();
                    byte[] bytes = new byte[8192];
                    int count;
                    while ((count = await stream.ReadAsync(bytes)) != 0)
                    {
                        if (buffer.Length + count > QueryPresetFormat.MaximumBytes)
                        {
                            throw new InvalidDataException("Preset files must be smaller than 2 MiB.");
                        }

                        buffer.Write(bytes, 0, count);
                    }
                    json = System.Text.Encoding.UTF8.GetString(buffer.ToArray()).TrimStart('\uFEFF');
                }
                if (json is null)
                {
                    return;
                }

                await host.QueryPresets.ImportAsync(json);
                await Refresh();
                status.Text = "Imported as new presets. Existing presets were kept.";
            });
            panel.Children.Add(new TextBlock { Text = "Query presets", FontWeight = FontWeight.SemiBold });
            panel.Children.Add(new TextBlock { Text = "Saved on this device/browser. Loading replaces the draft without running it. Export to back up or share.", TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(choices);
            panel.Children.Add(name);
            panel.Children.Add(variables);
            panel.Children.Add(new TextBlock { Text = "Queries and variables can contain sensitive data. Review before sharing.", TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(confirm);
            panel.Children.Add(actions);
            panel.Children.Add(status);
            flyout.ShowAt(button);
            try
            {
                await Refresh();
                status.Text = items.Length == 0 ? "No presets saved yet." : $"{items.Length} presets saved locally.";
            }
            catch (Exception ex)
            {
                status.Text = "Could not read saved presets: " + ex.Message;
            }
        };
    }

    private static readonly FilePickerFileType JsonFiles = new("Query preset JSON") { Patterns = ["*.json"], MimeTypes = ["application/json"] };
}
