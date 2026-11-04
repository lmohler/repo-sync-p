using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using RepoSyncV3.Models;
using RepoSyncV3.Services;
using RepoSyncV3.Views;

namespace RepoSyncV3.ViewModels;

public sealed partial class RepoDetailViewModel
{
    // =================================================================================== pipelines

    public ObservableCollection<ForgePipeline> Pipelines { get; } = new();
    public ObservableCollection<ForgeJob> Jobs { get; } = new();

    private bool _pipelinesAllBranches;
    private ForgePipeline? _selectedPipeline;
    private ForgeJob? _selectedJob;
    private string _jobLog = string.Empty;

    public bool PipelinesAllBranches
    {
        get => _pipelinesAllBranches;
        set
        {
            if (Set(ref _pipelinesAllBranches, value)) _ = LoadPipelinesAsync();
        }
    }

    public ForgePipeline? SelectedPipeline
    {
        get => _selectedPipeline;
        set
        {
            if (Set(ref _selectedPipeline, value)) _ = LoadJobsAsync();
        }
    }

    public ForgeJob? SelectedJob
    {
        get => _selectedJob;
        set
        {
            if (Set(ref _selectedJob, value)) _ = LoadJobLogAsync();
        }
    }

    public string JobLog { get => _jobLog; private set => Set(ref _jobLog, value); }

    private int _jobLogErrorLine = -1;

    /// <summary>0-based line of the first error in <see cref="JobLog"/> (failed jobs only), or -1.</summary>
    public int JobLogErrorLine { get => _jobLogErrorLine; private set { if (Set(ref _jobLogErrorLine, value)) OnPropertyChanged(nameof(HasJobLogError)); } }

    public bool HasJobLogError => JobLogErrorLine >= 0;

    private async Task LoadPipelinesAsync()
    {
        if (ClientOrMessage() is not { } client) return;
        var list = await _main.RunApiAsync("Pipelines", () => client.ListPipelinesAsync(Repo.Remote!, PipelinesAllBranches ? null : Repo.Branch));
        if (list is null) return;
        var keep = SelectedPipeline?.Id;
        Replace(Pipelines, list);
        _selectedPipeline = Pipelines.FirstOrDefault(p => p.Id == keep) ?? Pipelines.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedPipeline));
        await LoadJobsAsync();
        UpdatePoll();
    }

    private async Task LoadJobsAsync()
    {
        if (SelectedPipeline is not { } p || Repo.Remote is null || Forges.For(Repo.Remote) is not { } client)
        {
            Jobs.Clear();
            return;
        }
        var jobs = await _main.RunApiAsync("Jobs", () => client.ListJobsAsync(Repo.Remote, p.Id));
        if (jobs is null) return;
        var keep = SelectedJob?.Id;
        // run order: ids rise in the order the jobs were created
        Replace(Jobs, jobs.OrderBy(j => j.Id));
        _selectedJob = Jobs.FirstOrDefault(j => j.Id == keep) ?? Jobs.FirstOrDefault(j => j.Status == "failed") ?? Jobs.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedJob));
        await LoadJobLogAsync();
    }

    private async Task LoadJobLogAsync()
    {
        if (SelectedJob is not { } job || Repo.Remote is null || Forges.For(Repo.Remote) is not { } client)
        {
            JobLog = string.Empty;
            return;
        }
        try
        {
            var text = await client.GetJobLogAsync(Repo.Remote, job.Id);
            if (SelectedJob?.Id != job.Id) return;
            // the end of a log is where failures are; keep the last 4000 lines
            var lines = JobLogs.Clean(text).Split('\n');
            var log = lines.Length > 4000 ? $"(showing the last 4000 of {lines.Length} lines)\n" + string.Join('\n', lines[^4000..]) : string.Join('\n', lines);
            // a failed job opens at its first error; anything else at the end
            JobLogErrorLine = job.Kind == "failed" ? JobLogs.FirstErrorLine(log) : -1;
            JobLog = log;
        }
        catch (Exception ex)
        {
            JobLogErrorLine = -1;
            JobLog = $"Could not load the job log: {ex.Message}";
        }
    }

    private async Task PipelineApi(string title, Func<IForge, ForgeRemote, Task> call)
    {
        var client = Forges.Require(Repo.Remote!);
        if (!await _main.RunApiAsync(title, () => call(client, Repo.Remote!))) return;
        _main.AddLog($"{Repo.Name}: {title} - done.", LogLevel.Success);
        await LoadPipelinesAsync();
        await _main.RefreshRepoAsync(Repo);
    }

    private void UpdatePoll()
    {
        var active = SelectedTab == TabPipelines && Pipelines.Any(p => p.Kind is "running" or "pending");
        if (active && !_pipelinePoll.IsEnabled) _pipelinePoll.Start();
        if (!active) _pipelinePoll.Stop();
    }

    private async Task PollPipelinesAsync()
    {
        if (SelectedTab != TabPipelines || _main.SelectedRepo != Repo)
        {
            _pipelinePoll.Stop();
            return;
        }
        await LoadPipelinesAsync();
    }

    public void Detach()
    {
        _pipelinePoll.Stop();
        Repo.PropertyChanged -= Repo_PropertyChanged;
    }
}
