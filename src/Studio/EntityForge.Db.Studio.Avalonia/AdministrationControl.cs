using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using EntityForge.Db.AppModel.Connections;

namespace EntityForge.Db.Studio.Avalonia;

internal sealed class AdministrationControl : StackPanel
{
    public AdministrationControl(EntityForgeConnectionSession connection, bool optimization = false)
    {
        Spacing = 10;
        AdministrationPlan? plan = null;
        bool preparing = false;
        AdministrationReceipt? shownReceipt = null;
        AdministrationPlan? shownPlan = null;
        string? shownError = null;
        bool shownBusy = false, renderedOutcome = false;
        string[] operations = optimization ? ["optimize", "reset-observations"] : ["checkpoint", "backup"];
        string[] labels = optimization ? ["Apply one server-selected recommendation", "Clear workload observations"] : ["Checkpoint", "Backup"];
        var operation = new ComboBox { ItemsSource = labels, SelectedIndex = 0 };
        var name = new TextBox { Text = "database-backup", PlaceholderText = "Backup name", IsVisible = false };
        var review = new Button { Content = "Review target" };
        var target = new StackPanel { Spacing = 6 };
        var confirm = new CheckBox { Content = InspectionView.Text("I have reviewed the target and recorded the job ID"), IsVisible = false };
        var run = new Button { Content = "Run reviewed operation", IsVisible = false };
        var error = InspectionView.Text("", "ef-warning");
        var jobId = new TextBox { PlaceholderText = "Administration job ID", Text = connection.LastAdministrationPlan?.Id.ToString("D") };
        var lookup = new Button { Content = "Check outcome" };
        var stop = new Button { Content = "Stop waiting" };
        var outcome = new StackPanel { Spacing = 8 };
        var availability = InspectionView.Text("");
        Children.Add(availability);
        Children.Add(new WrapPanel { ItemSpacing = 8, LineSpacing = 6, Children = { operation, review } });
        Children.Add(name);
        Children.Add(target);
        Children.Add(confirm);
        Children.Add(run);
        Children.Add(error);
        Children.Add(stop);
        Children.Add(outcome);
        Children.Add(new Expander
        {
            Header = "Look up a job",
            Content = new StackPanel
            {
                Spacing = 6,
                Children =
            { InspectionView.Text("Administration job ID"), jobId, lookup }
            }
        });
        global::Avalonia.Automation.AutomationProperties.SetName(operation, "Administration operation");
        global::Avalonia.Automation.AutomationProperties.SetName(name, "Backup name");
        global::Avalonia.Automation.AutomationProperties.SetName(jobId, "Administration job ID");
        void ClearReview()
        {
            plan = null;
            confirm.IsChecked = false;
            confirm.IsVisible = run.IsVisible = false;
            target.Children.Clear();
        }
        void Render()
        {
            bool available = (optimization ? connection.SupportsOptimizationJobs : connection.SupportsAdministrationJobs) && connection.CanAdminister != false;
            availability.Text = connection.Current is null ? "Connect to a database to review an administration target."
                : connection.CanAdminister == false ? "Administration is denied for this credential."
                : !(optimization ? connection.SupportsOptimizationJobs : connection.SupportsAdministrationJobs) ? "This host does not advertise retained jobs for these actions."
                : optimization ? "Review an action before applying it."
                : operation.SelectedIndex == 1 ? "Verified backup · saved on the server" : "Checkpoint · current database";
            review.IsEnabled = available && !connection.AdministrationBusy && !preparing;
            operation.IsEnabled = name.IsEnabled = !preparing && !connection.AdministrationBusy;
            lookup.IsEnabled = available && !connection.AdministrationBusy;
            run.IsEnabled = available && !connection.AdministrationBusy && confirm.IsChecked == true && plan is not null;
            stop.IsVisible = connection.AdministrationBusy;
            error.IsVisible = !string.IsNullOrEmpty(error.Text);
            if (plan is not null && (connection.Current?.Profile.Endpoint != plan.Endpoint || connection.Current.Capabilities.DatabaseIncarnation != plan.DatabaseIncarnation))
            {
                ClearReview();
            }

            if (renderedOutcome && ReferenceEquals(shownReceipt, connection.AdministrationReceipt) && ReferenceEquals(shownPlan, connection.LastAdministrationPlan)
                && shownError == connection.AdministrationError && shownBusy == connection.AdministrationBusy)
            {
                return;
            }

            renderedOutcome = true;
            shownReceipt = connection.AdministrationReceipt;
            shownPlan = connection.LastAdministrationPlan;
            shownError = connection.AdministrationError;
            shownBusy = connection.AdministrationBusy;
            outcome.Children.Clear();
            if (connection.AdministrationBusy)
            {
                outcome.Children.Add(new ProgressBar { IsIndeterminate = true, Height = 3 });
            }

            if (connection.AdministrationError is { } message)
            {
                outcome.Children.Add(InspectionView.Text(message, "ef-warning"));
            }

            if (connection.AdministrationReceipt is { } receipt)
            {
                string description = receipt.State switch
                {
                    "Succeeded" => receipt.Operation switch
                    {
                        "backup" => "Verified backup created on the server.",
                        "optimize" => receipt.Result is { } applied && applied.TryGetProperty("appliedPlacements", out var placements) && placements.ValueKind == System.Text.Json.JsonValueKind.Array
                            ? $"{placements.GetArrayLength()} placement change(s) applied." : "Optimization completed.",
                        "reset-observations" => "Workload observations cleared.",
                        _ => "Durable checkpoint created."
                    },
                    "Running" => "The host is still working. Check this same job ID again.",
                    "Unknown" => "No terminal outcome was recorded. Inspect the target before taking further action.",
                    _ => "The host reports an unsuccessful job. Work may have partially completed."
                };
                outcome.Children.Add(InspectionView.Card(new StackPanel
                {
                    Spacing = 6,
                    Children =
                {
                    InspectionView.Text(receipt.State, "ef-section-title"), InspectionView.Text(description),
                    new TextBox { Text = receipt.Target, IsReadOnly = true },
                    InspectionView.Text($"Updated {receipt.UpdatedAt.LocalDateTime:G}")
                }
                }));
                if (receipt.Message is { } detail)
                {
                    outcome.Children.Add(InspectionView.Text(detail));
                }

                if (receipt.Result is { } result)
                {
                    outcome.Children.Add(NativeResultInspector.Create("Receipt details", result));
                }
            }
            if (connection.LastAdministrationPlan is { } last)
            {
                outcome.Children.Add(new Expander
                {
                    Header = "Job details",
                    Content = new StackPanel
                    {
                        Spacing = 8,
                        Children =
                {
                    InspectionView.Field("Job ID", last.Id.ToString("D")), InspectionView.Field("Server", last.Endpoint.ToString()),
                    InspectionView.Field("Database", last.DatabaseIncarnation.ToString("D"))
                }
                    }
                });
            }
        }
        operation.SelectionChanged += (_, _) =>
        {
            name.IsVisible = !optimization && operation.SelectedIndex == 1;
            ClearReview();
            Render();
        };
        name.TextChanged += (_, _) =>
        {
            ClearReview();
            Render();
        };
        confirm.IsCheckedChanged += (_, _) => Render();
        review.Click += async (_, _) =>
        {
            preparing = true;
            ClearReview();
            error.Text = "";
            Render();
            try
            {
                plan = await connection.PrepareAdministrationAsync(operations[operation.SelectedIndex], name.Text);
                target.Children.Add(InspectionView.Text(labels[operation.SelectedIndex], "ef-section-title"));
                target.Children.Add(InspectionView.Field("Server", plan.Endpoint.ToString()));
                target.Children.Add(InspectionView.Field("Database", plan.DatabaseIncarnation.ToString("D")));
                target.Children.Add(InspectionView.Text(plan.Operation switch
                {
                    "optimize" => "The server selects at most one placement from its current workload; it may differ from the displayed report. Logical values and evidence are unchanged.",
                    "reset-observations" => "Clears collected workload counters. Data and existing placements are unchanged.",
                    "backup" => "Creates a verified standalone backup at this server-side destination.",
                    _ => "Persists a checkpoint of this database. A checkpoint is not an independent backup."
                }));
                target.Children.Add(new TextBox { Text = plan.ExpectedTarget, IsReadOnly = true });
                target.Children.Add(InspectionView.Text("Job ID"));
                target.Children.Add(new TextBox { Text = plan.Id.ToString("D"), IsReadOnly = true });
                confirm.IsVisible = run.IsVisible = true;
            }
            catch (Exception ex)
            {
                error.Text = ex.Message;
                error.IsVisible = true;
            }
            finally
            {
                preparing = false;
                Render();
            }
        };
        run.Click += async (_, _) =>
        {
            if (plan is null || confirm.IsChecked != true)
            {
                return;
            }

            var reviewed = plan;
            jobId.Text = reviewed.Id.ToString("D");
            ClearReview();
            error.Text = "";
            try
            {
                await connection.RunAdministrationAsync(reviewed);
            }
            catch (Exception ex)
            {
                error.Text = ex.Message;
                error.IsVisible = true;
            }
            Render();
        };
        lookup.Click += async (_, _) =>
        {
            error.Text = "";
            if (!Guid.TryParse(jobId.Text, out var id) || id == Guid.Empty)
            {
                error.Text = "Enter a valid job ID.";
                error.IsVisible = true;
                return;
            }
            try
            {
                await connection.ResolveAdministrationAsync(id);
            }
            catch (Exception ex)
            {
                error.Text = ex.Message;
            }
            Render();
        };
        stop.Click += (_, _) => connection.StopAdministrationWait();
        void Changed(object? sender, EventArgs e) => Dispatcher.UIThread.Post(Render);
        AttachedToVisualTree += (_, _) =>
        {
            connection.AdministrationChanged += Changed;
            connection.AccessChanged += Changed;
            connection.Changed += Changed;
            Render();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            connection.AdministrationChanged -= Changed;
            connection.AccessChanged -= Changed;
            connection.Changed -= Changed;
        };
        Render();
    }
}
