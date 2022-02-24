﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.Extensions.Logging;
using OmniSharp.Helpers;
using OmniSharp.Models.Diagnostics;
using OmniSharp.Models.Events;
using OmniSharp.Options;
using OmniSharp.Roslyn.CSharp.Workers.Diagnostics;
using OmniSharp.Services;

namespace OmniSharp.Roslyn.CSharp.Services.Diagnostics
{
    public class CSharpDiagnosticWorkerWithAnalyzers : ICsDiagnosticWorker, IDisposable
    {
        private readonly AsyncAnalyzerWorkQueue _workQueue;
        private readonly ILogger<CSharpDiagnosticWorkerWithAnalyzers> _logger;

        private readonly ConcurrentDictionary<DocumentId, DocumentDiagnostics> _currentDiagnosticResultLookup = new();
        private readonly ImmutableArray<ICodeActionProvider> _providers;
        private readonly DiagnosticEventForwarder _forwarder;
        private readonly OmniSharpOptions _options;
        private readonly OmniSharpWorkspace _workspace;
        private readonly ImmutableArray<Task> _workerTasks;

        // This is workaround.
        // Currently roslyn doesn't expose official way to use IDE analyzers during analysis.
        // This options gives certain IDE analysis access for services that are not yet publicly available.
        private readonly ConstructorInfo _workspaceAnalyzerOptionsConstructor;

        public CSharpDiagnosticWorkerWithAnalyzers(
            OmniSharpWorkspace workspace,
            [ImportMany] IEnumerable<ICodeActionProvider> providers,
            ILoggerFactory loggerFactory,
            DiagnosticEventForwarder forwarder,
            OmniSharpOptions options)
        {
            _logger = loggerFactory.CreateLogger<CSharpDiagnosticWorkerWithAnalyzers>();
            _providers = providers.ToImmutableArray();
            _workQueue = new AsyncAnalyzerWorkQueue(loggerFactory);

            _forwarder = forwarder;
            _options = options;
            _workspace = workspace;

            _workspaceAnalyzerOptionsConstructor = Assembly
                .Load("Microsoft.CodeAnalysis.Features")
                .GetType("Microsoft.CodeAnalysis.Diagnostics.WorkspaceAnalyzerOptions")
                .GetConstructor(new Type[] { typeof(AnalyzerOptions), typeof(Solution) })
                ?? throw new InvalidOperationException("Could not resolve 'Microsoft.CodeAnalysis.Diagnostics.WorkspaceAnalyzerOptions' for IDE analyzers.");

            _workspace.WorkspaceChanged += OnWorkspaceChanged;
            _workspace.OnInitialized += OnWorkspaceInitialized;

            _workerTasks = Enumerable.Range(0, options.RoslynExtensionsOptions.DiagnosticWorkersThreadCount)
                .Select(_ => Task.Run(Worker))
                .ToImmutableArray();

            OnWorkspaceInitialized(_workspace.Initialized);
        }

        public void OnWorkspaceInitialized(bool isInitialized)
        {
            if (isInitialized)
            {
                var documentIds = QueueDocumentsForDiagnostics();
                _logger.LogInformation($"Solution initialized -> queue all documents for code analysis. Initial document count: {documentIds.Length}.");
            }
        }

        public async Task<ImmutableArray<DocumentDiagnostics>> GetDiagnostics(ImmutableArray<string> documentPaths)
        {
            var documentIds = GetDocumentIdsFromPaths(documentPaths);

            return await GetDiagnosticsByDocumentIds(documentIds, waitForDocuments: true);
        }

        private async Task<ImmutableArray<DocumentDiagnostics>> GetDiagnosticsByDocumentIds(ImmutableArray<DocumentId> documentIds, bool waitForDocuments)
        {
            if (waitForDocuments)
            {
                foreach (var documentId in documentIds)
                {
                    _workQueue.TryPromote(documentId);
                }

                using var cancellationTokenSource = new CancellationTokenSource(_options.RoslynExtensionsOptions.DocumentAnalysisTimeoutMs * 3);

                await _workQueue.WaitForegroundWorkComplete(cancellationTokenSource.Token);
            }

            return documentIds
                .Where(x => _currentDiagnosticResultLookup.ContainsKey(x))
                .Select(x => _currentDiagnosticResultLookup[x])
                .ToImmutableArray();
        }

        private ImmutableArray<DocumentId> GetDocumentIdsFromPaths(ImmutableArray<string> documentPaths)
        {
            return documentPaths
                .Select(docPath => _workspace.GetDocumentId(docPath))
                .Where(x => x != default)
                .ToImmutableArray();
        }

        private async Task Worker()
        {
            while (true)
            {
                AsyncAnalyzerWorkQueue.QueueItem item = null;
                DocumentId documentId;
                CancellationToken? cancellationToken = null;
                AnalyzerWorkType workType;
                int documentCount;
                int remaining;

                try
                {
                    item = await _workQueue.TakeWorkAsync();
                    (documentId, cancellationToken, workType, documentCount, remaining) = item;

                    if (workType == AnalyzerWorkType.Background)
                    {
                        // event every percentage increase, or every 10th if there are fewer than 1000
                        var eventEvery = Math.Max(10, documentCount / 100);

                        if (documentCount == remaining + 1)
                            EventIfBackgroundWork(workType, BackgroundDiagnosticStatus.Started, _projectCount, documentCount, remaining);

                        var done = documentCount - remaining;
                        if (done % eventEvery == 0 || remaining == 0)
                            EventIfBackgroundWork(workType, BackgroundDiagnosticStatus.Progress, _projectCount, documentCount, remaining);
                    }

                    var solution = _workspace.CurrentSolution;
                    var projectId = solution.GetDocument(documentId)?.Project?.Id;

                    try
                    {
                        if (projectId != null)
                            await AnalyzeDocument(solution, projectId, documentId, cancellationToken.Value);
                    }
                    finally
                    {
                        if (remaining == 0)
                            EventIfBackgroundWork(workType, BackgroundDiagnosticStatus.Finished, _projectCount, documentCount, remaining);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken != null && cancellationToken.Value.IsCancellationRequested)
                {
                    _logger.LogInformation($"Analyzer work cancelled.");
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Analyzer worker failed: {ex}");
                }
                finally
                {
                    if (item != null)
                        _workQueue.WorkComplete(item);
                }
            }
        }

        private void EventIfBackgroundWork(AnalyzerWorkType workType, BackgroundDiagnosticStatus status, int numberProjects, int numberFiles, int numberFilesRemaining)
        {
            if (workType == AnalyzerWorkType.Background)
                _forwarder.BackgroundDiagnosticsStatus(status, numberProjects, numberFiles, numberFilesRemaining);
        }

        private int _projectCount = 0;

        private void QueueForAnalysis(ImmutableArray<DocumentId> documentIds, AnalyzerWorkType workType)
        {
            if (workType == AnalyzerWorkType.Background)
            {
                var solution = _workspace.CurrentSolution;

                _projectCount = documentIds.Select(x => solution.GetDocument(x)?.Project?.Id).Distinct().Count(x => x != null);
            }

            _workQueue.PutWork(documentIds, workType);
        }

        private void OnWorkspaceChanged(object sender, WorkspaceChangeEventArgs changeEvent)
        {
            switch (changeEvent.Kind)
            {
                case WorkspaceChangeKind.DocumentChanged:
                case WorkspaceChangeKind.DocumentAdded:
                case WorkspaceChangeKind.DocumentReloaded:
                case WorkspaceChangeKind.DocumentInfoChanged:
                    QueueForAnalysis(ImmutableArray.Create(changeEvent.DocumentId), AnalyzerWorkType.Foreground);
                    break;
                case WorkspaceChangeKind.DocumentRemoved:
                    if (!_currentDiagnosticResultLookup.TryRemove(changeEvent.DocumentId, out _))
                    {
                        _logger.LogDebug($"Tried to remove non existent document from analysis, document: {changeEvent.DocumentId}");
                    }
                    break;
                case WorkspaceChangeKind.AnalyzerConfigDocumentChanged:
                    _logger.LogDebug($"Analyzer config document {changeEvent.DocumentId} changed, which triggered re-analysis of project {changeEvent.ProjectId}.");
                    QueueForAnalysis(_workspace.CurrentSolution.GetProject(changeEvent.ProjectId).Documents.Select(x => x.Id).ToImmutableArray(), AnalyzerWorkType.Background);
                    break;
                case WorkspaceChangeKind.ProjectAdded:
                case WorkspaceChangeKind.ProjectChanged:
                case WorkspaceChangeKind.ProjectReloaded:
                    _logger.LogDebug($"Project {changeEvent.ProjectId} updated, reanalyzing its diagnostics.");
                    QueueForAnalysis(_workspace.CurrentSolution.GetProject(changeEvent.ProjectId).Documents.Select(x => x.Id).ToImmutableArray(), AnalyzerWorkType.Background);
                    break;
                case WorkspaceChangeKind.SolutionAdded:
                case WorkspaceChangeKind.SolutionChanged:
                case WorkspaceChangeKind.SolutionReloaded:
                    QueueDocumentsForDiagnostics();
                    break;
            }
        }

        public async Task<IEnumerable<Diagnostic>> AnalyzeDocumentAsync(Document document, CancellationToken cancellationToken)
        {
            Project project = document.Project;
            var allAnalyzers = GetAnalyzersForProject(project);
            var compilation = await project.GetCompilationAsync(cancellationToken);
            var workspaceAnalyzerOptions = (AnalyzerOptions)_workspaceAnalyzerOptionsConstructor.Invoke(new object[] { project.AnalyzerOptions, project.Solution });

            return await AnalyzeDocument(project, allAnalyzers, compilation, workspaceAnalyzerOptions, document, cancellationToken);
        }

        public async Task<IEnumerable<Diagnostic>> AnalyzeProjectsAsync(Project project, CancellationToken cancellationToken)
        {
            QueueForAnalysis(project.DocumentIds.ToImmutableArray(), AnalyzerWorkType.Foreground);

            await _workQueue.WaitForegroundWorkComplete(cancellationToken);

            return _currentDiagnosticResultLookup
                .SelectMany(X => X.Value.Diagnostics)
                .ToImmutableArray();
        }

        private async Task AnalyzeDocument(Solution solution, ProjectId projectId, DocumentId documentId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var project = solution.GetProject(projectId);
                var allAnalyzers = GetAnalyzersForProject(project);
                var compilation = await project.GetCompilationAsync();
                var workspaceAnalyzerOptions = (AnalyzerOptions)_workspaceAnalyzerOptionsConstructor.Invoke(new object[] { project.AnalyzerOptions, project.Solution });
                var document = project.GetDocument(documentId);

                var diagnostics = await AnalyzeDocument(project, allAnalyzers, compilation, workspaceAnalyzerOptions, document, cancellationToken);

                UpdateCurrentDiagnostics(project, document, diagnostics);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Analysis of project {documentId} failed, underlaying error: {ex}");
            }
        }

        private async Task<ImmutableArray<Diagnostic>> AnalyzeDocument(Project project, ImmutableArray<DiagnosticAnalyzer> allAnalyzers, Compilation compilation, AnalyzerOptions workspaceAnalyzerOptions, Document document, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // There's real possibility that bug in analyzer causes analysis hang at document.
            using var perDocumentTimeout =
                new CancellationTokenSource(_options.RoslynExtensionsOptions.DocumentAnalysisTimeoutMs);
            using var combinedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, perDocumentTimeout.Token);

            try
            {
                var documentSemanticModel = await document.GetSemanticModelAsync(combinedCancellation.Token);

                // Only basic syntax check is available if file is miscellanous like orphan .cs file.
                // Those projects are on hard coded virtual project
                if (project.Name == $"{Configuration.OmniSharpMiscProjectName}.csproj")
                {
                    var syntaxTree = await document.GetSyntaxTreeAsync(combinedCancellation.Token);
                    return syntaxTree.GetDiagnostics(cancellationToken: combinedCancellation.Token).ToImmutableArray();
                }
                else if (allAnalyzers.Any()) // Analyzers cannot be called with empty analyzer list.
                {
                    var compilationWithAnalyzers = compilation.WithAnalyzers(allAnalyzers, new CompilationWithAnalyzersOptions(
                        workspaceAnalyzerOptions,
                        onAnalyzerException: OnAnalyzerException,
                        concurrentAnalysis: false,
                        logAnalyzerExecutionTime: false,
                        reportSuppressedDiagnostics: false));

                    var semanticDiagnosticsWithAnalyzers = await compilationWithAnalyzers
                        .GetAnalyzerSemanticDiagnosticsAsync(documentSemanticModel, filterSpan: null, combinedCancellation.Token);

                    var syntaxDiagnosticsWithAnalyzers = await compilationWithAnalyzers
                        .GetAnalyzerSyntaxDiagnosticsAsync(documentSemanticModel.SyntaxTree, combinedCancellation.Token);

                    return semanticDiagnosticsWithAnalyzers
                        .Concat(syntaxDiagnosticsWithAnalyzers)
                        .Where(d => !d.IsSuppressed)
                        .Concat(documentSemanticModel.GetDiagnostics(cancellationToken: combinedCancellation.Token))
                        .ToImmutableArray();
                }
                else
                {
                    return documentSemanticModel.GetDiagnostics(cancellationToken: combinedCancellation.Token);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Analysis of document {document.Name} failed or cancelled by timeout: {ex.Message}, analysers: {string.Join(", ", allAnalyzers)}");
                return ImmutableArray<Diagnostic>.Empty;
            }
        }

        private ImmutableArray<DiagnosticAnalyzer> GetAnalyzersForProject(Project project)
        {
            return _providers
                .SelectMany(x => x.CodeDiagnosticAnalyzerProviders)
                .Concat(project.AnalyzerReferences.SelectMany(x => x.GetAnalyzers(project.Language)))
                .ToImmutableArray();
        }

        private void OnAnalyzerException(Exception ex, DiagnosticAnalyzer analyzer, Diagnostic diagnostic)
        {
            _logger.LogDebug("Exception in diagnostic analyzer." +
                $"\n            analyzer: {analyzer}" +
                $"\n            diagnostic: {diagnostic}" +
                $"\n            exception: {ex.Message}");
        }

        private void UpdateCurrentDiagnostics(Project project, Document document, ImmutableArray<Diagnostic> diagnostics)
        {
            var documentDiagnostics = new DocumentDiagnostics(document.Id, document.FilePath, project.Id, project.Name, diagnostics);
            _currentDiagnosticResultLookup[document.Id] = documentDiagnostics;
            EmitDiagnostics(documentDiagnostics);
        }

        private void EmitDiagnostics(DocumentDiagnostics results)
        {
            _forwarder.Forward(new DiagnosticMessage
            {
                Results = new[]
                {
                    new DiagnosticResult
                    {
                        FileName = results.DocumentPath, QuickFixes = results.Diagnostics
                            .Select(x => x.ToDiagnosticLocation())
                            .ToList()
                    }
                }
            });
        }

        public ImmutableArray<DocumentId> QueueDocumentsForDiagnostics()
        {
            var documentIds = _workspace.CurrentSolution.Projects.SelectMany(x => x.DocumentIds).ToImmutableArray();
            QueueForAnalysis(documentIds, AnalyzerWorkType.Background);
            return documentIds;
        }

        public async Task<ImmutableArray<DocumentDiagnostics>> GetAllDiagnosticsAsync()
        {
            var allDocumentsIds = _workspace.CurrentSolution.Projects.SelectMany(x => x.DocumentIds).ToImmutableArray();
            return await GetDiagnosticsByDocumentIds(allDocumentsIds, waitForDocuments: false);
        }

        public ImmutableArray<DocumentId> QueueDocumentsForDiagnostics(ImmutableArray<ProjectId> projectIds)
        {
            var documentIds = projectIds
                .SelectMany(projectId => _workspace.CurrentSolution.GetProject(projectId).Documents.Select(x => x.Id))
                .ToImmutableArray();
            QueueForAnalysis(documentIds, AnalyzerWorkType.Background);
            return documentIds;
        }

        public void Dispose()
        {
            _workspace.WorkspaceChanged -= OnWorkspaceChanged;
            _workspace.OnInitialized -= OnWorkspaceInitialized;

            // TODO: Clean up worker queue
            // TODO: Clean up background threads
        }
    }
}
