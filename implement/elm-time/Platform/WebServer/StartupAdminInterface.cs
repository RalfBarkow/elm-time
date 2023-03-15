using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Net.Http.Headers;
using System.Text;
using ElmTime.Platform.WebServer.ProcessStoreSupportingMigrations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pine;

namespace ElmTime.Platform.WebServer;

public class StartupAdminInterface
{
    static public string PathApiDeployAndInitAppState => "/api/deploy-and-init-app-state";

    static public string PathApiDeployAndMigrateAppState => "/api/deploy-and-migrate-app-state";

    static public string PathApiRevertProcessTo => "/api/revert-process-to";

    static public string PathApiElmAppState => "/api/elm-app-state";

    static public string PathApiGetDeployedAppConfig => "/api/get-deployed-app-config";

    static public string PathApiReplaceProcessHistory => "/api/replace-process-history";

    static public string PathApiTruncateProcessHistory => "/api/truncate-process-history";

    static public string PathApiProcessHistoryFileStore => "/api/process-history-file-store";

    static public string PathApiProcessHistoryFileStoreGetFileContent => PathApiProcessHistoryFileStore + "/get-file-content";

    static public string PathApiProcessHistoryFileStoreListFilesInDirectory => PathApiProcessHistoryFileStore + "/list-files-in-directory";

    static public string PathApiApplyFunctionOnDatabase => "/api/apply-function-on-db/";

    static public IImmutableList<string> WebServerConfigFilePathDefault => ImmutableList.Create("web-server.json");

    static public IImmutableList<IImmutableList<string>> WebServerConfigFilePathAlternatives =>
        ImmutableList.Create(
            WebServerConfigFilePathDefault,

            /*
             * Support smooth migration of projects with backwards compatibility here:
             * Support the name used before 2023 as alternative.
             * */
            ImmutableList.Create("elm-web-server.json"),
            ImmutableList.Create("elm-fullstack.json"));

    private readonly ILogger<StartupAdminInterface> logger;

    public StartupAdminInterface(ILogger<StartupAdminInterface> logger)
    {
        this.logger = logger;

        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (sender, eventArgs) =>
        {
            logger.LogError(eventArgs.Exception, "Unobserved task exception in sender {Sender}", sender?.ToString());
        };
    }

    public static void ConfigureServices(IServiceCollection services)
    {
        var serviceProvider = services.BuildServiceProvider();

        var getDateTimeOffset = serviceProvider.GetService<Func<DateTimeOffset>>();

        if (getDateTimeOffset == null)
        {
            getDateTimeOffset = () => DateTimeOffset.UtcNow;
            services.AddSingleton(getDateTimeOffset);
        }
    }

    record PublicHostConfiguration(
        PersistentProcessLiveRepresentation processLiveRepresentation,
        IHost webHost);

    public void Configure(
        IApplicationBuilder app,
        IWebHostEnvironment env,
        IHostApplicationLifetime appLifetime,
        Func<DateTimeOffset> getDateTimeOffset,
        FileStoreForProcessStore processStoreForFileStore)
    {
        if (env.IsDevelopment())
        {
            app.UseDeveloperExceptionPage();
        }

        var configuration = app.ApplicationServices.GetService<IConfiguration>();

        var adminPassword = configuration?.GetValue<string>(Configuration.AdminPasswordSettingKey);

        object avoidConcurrencyLock = new();

        var processStoreFileStore = processStoreForFileStore.fileStore;

        PublicHostConfiguration? publicAppHost = null;

        void stopPublicApp()
        {
            lock (avoidConcurrencyLock)
            {
                if (publicAppHost != null)
                {
                    logger.LogInformation("Begin to stop the public app.");

                    publicAppHost?.webHost?.StopAsync(TimeSpan.FromSeconds(10)).Wait();
                    publicAppHost?.webHost?.Dispose();
                    publicAppHost?.processLiveRepresentation?.Dispose();
                    publicAppHost = null;
                }
            }
        }

        appLifetime.ApplicationStopping.Register(stopPublicApp);

        var processStoreWriter =
            new ProcessStoreWriterInFileStore(
                processStoreFileStore,
                getTimeForCompositionLogBatch: getDateTimeOffset,
                processStoreFileStore);

        void startPublicApp()
        {
            lock (avoidConcurrencyLock)
            {
                stopPublicApp();

                logger.LogInformation("Begin to build the process live representation.");

                var restoreProcessResult =
                    PersistentProcessLiveRepresentation.LoadFromStoreAndRestoreProcess(
                        new ProcessStoreReaderInFileStore(processStoreFileStore),
                        logger: logEntry => logger.LogInformation(logEntry));

                var processLiveRepresentation = restoreProcessResult.process;

                logger.LogInformation("Completed building the process live representation.");

                var cyclicReductionStoreLock = new object();
                DateTimeOffset? cyclicReductionStoreLastTime = null;
                var cyclicReductionStoreDistanceSeconds = (int)TimeSpan.FromMinutes(10).TotalSeconds;

                void maintainStoreReductions()
                {
                    var currentDateTime = getDateTimeOffset();

                    System.Threading.Thread.MemoryBarrier();
                    var cyclicReductionStoreLastAge = currentDateTime - cyclicReductionStoreLastTime;

                    if (!(cyclicReductionStoreLastAge?.TotalSeconds < cyclicReductionStoreDistanceSeconds))
                    {
                        if (System.Threading.Monitor.TryEnter(cyclicReductionStoreLock))
                        {
                            try
                            {
                                var afterLockCyclicReductionStoreLastAge = currentDateTime - cyclicReductionStoreLastTime;

                                if (afterLockCyclicReductionStoreLastAge?.TotalSeconds < cyclicReductionStoreDistanceSeconds)
                                    return;

                                lock (avoidConcurrencyLock)
                                {
                                    var (reductionRecord, _) = processLiveRepresentation.StoreReductionRecordForCurrentState(processStoreWriter!);
                                }

                                cyclicReductionStoreLastTime = currentDateTime;
                                System.Threading.Thread.MemoryBarrier();
                            }
                            finally
                            {
                                System.Threading.Monitor.Exit(cyclicReductionStoreLock);
                            }
                        }
                    }
                }

                IHost buildWebHost(
                    ProcessAppConfig processAppConfig,
                    IReadOnlyList<string> publicWebHostUrls)
                {
                    var appConfigTree =
                        Composition.ParseAsTreeWithStringPath(processAppConfig.appConfigComponent)
                        .Extract(error => throw new Exception(error.ToString()));

                    var appConfigFilesNamesAndContents =
                        appConfigTree.EnumerateBlobsTransitive();

                    var webServerConfigFile =
                        appConfigFilesNamesAndContents
                        .Where(filePathAndContent => WebServerConfigFilePathAlternatives.Any(configFilePath => filePathAndContent.path.SequenceEqual(configFilePath)))
                        .Select(filePathAndContent => filePathAndContent.blobContent)
                        .Cast<ReadOnlyMemory<byte>?>()
                        .FirstOrDefault();

                    var webServerConfig =
                        webServerConfigFile == null
                        ?
                        null
                        :
                        System.Text.Json.JsonSerializer.Deserialize<WebServerConfigJson>(Encoding.UTF8.GetString(webServerConfigFile.Value.Span));

                    var serverAndElmAppConfig =
                        new ServerAndElmAppConfig(
                            ServerConfig: webServerConfig,
                            ProcessEventInElmApp: serializedEvent =>
                            {
                                lock (avoidConcurrencyLock)
                                {
                                    var elmEventResponse =
                                        processLiveRepresentation.ProcessElmAppEvent(
                                            processStoreWriter!, serializedEvent);

                                    maintainStoreReductions();

                                    return elmEventResponse;
                                }
                            },
                            SourceComposition: processAppConfig.appConfigComponent,
                            InitOrMigrateCmds: restoreProcessResult.initOrMigrateCmds
                        );

                    var publicAppState = new PublicAppState(
                        serverAndElmAppConfig: serverAndElmAppConfig,
                        getDateTimeOffset: getDateTimeOffset);

                    var appBuilder = WebApplication.CreateBuilder();

                    var app =
                        publicAppState.Build(
                            appBuilder,
                            env,
                            publicWebHostUrls: publicWebHostUrls);

                    publicAppState.ProcessEventTimeHasArrived();

                    return app;
                }

                if (processLiveRepresentation?.lastAppConfig != null)
                {
                    var publicWebHostUrls = configuration.GetSettingPublicWebHostUrls();

                    var webHost = buildWebHost(
                        processLiveRepresentation.lastAppConfig,
                        publicWebHostUrls: publicWebHostUrls);

                    webHost.StartAsync(appLifetime.ApplicationStopping).Wait();

                    logger.LogInformation("Started the public app at '" + string.Join(",", publicWebHostUrls) + "'.");

                    publicAppHost = new PublicHostConfiguration(
                        processLiveRepresentation: processLiveRepresentation,
                        webHost: webHost);
                }
            }
        }

        startPublicApp();

        app.Run(async (context) =>
            {
                var syncIOFeature = context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpBodyControlFeature>();
                if (syncIOFeature != null)
                {
                    syncIOFeature.AllowSynchronousIO = true;
                }

                {
                    context.Request.Headers.TryGetValue("Authorization", out var requestAuthorizationHeaderValue);

                    context.Response.Headers.Add("X-Powered-By", "Elm-Time " + Program.AppVersionId);

                    AuthenticationHeaderValue.TryParse(
                        requestAuthorizationHeaderValue.FirstOrDefault(), out var requestAuthorization);

                    if (!(0 < adminPassword?.Length))
                    {
                        context.Response.StatusCode = 403;
                        await context.Response.WriteAsync("The admin interface is not available because the admin password is not yet configured.");
                        return;
                    }

                    var buffer = new byte[400];

                    var decodedRequestAuthorizationParameter =
                        Convert.TryFromBase64String(requestAuthorization?.Parameter ?? "", buffer, out var bytesWritten) ?
                        Encoding.UTF8.GetString(buffer, 0, bytesWritten) : null;

                    var requestAuthorizationPassword =
                        decodedRequestAuthorizationParameter?.Split(':')?.ElementAtOrDefault(1);

                    if (!(string.Equals(adminPassword, requestAuthorizationPassword) &&
                        string.Equals("basic", requestAuthorization?.Scheme, StringComparison.OrdinalIgnoreCase)))
                    {
                        context.Response.StatusCode = 401;
                        context.Response.Headers.Add(
                            "WWW-Authenticate",
                            @"Basic realm=""" + context.Request.Host + @""", charset=""UTF-8""");
                        await context.Response.WriteAsync("Unauthorized");
                        return;
                    }
                }

                async System.Threading.Tasks.Task deployElmApp(bool initElmAppState)
                {
                    var memoryStream = new MemoryStream();
                    context.Request.Body.CopyTo(memoryStream);

                    var deploymentZipArchive = memoryStream.ToArray();

                    {
                        try
                        {
                            var filesFromZipArchive = ZipArchive.EntriesFromZipArchive(deploymentZipArchive).ToImmutableList();

                            if (filesFromZipArchive.Count < 1)
                                throw new Exception("Contains no files.");
                        }
                        catch (Exception e)
                        {
                            context.Response.StatusCode = 400;
                            await context.Response.WriteAsync("Malformed web app config zip-archive:\n" + e);
                            return;
                        }
                    }

                    var deploymentTree =
                        Composition.SortedTreeFromSetOfBlobsWithCommonFilePath(
                            ZipArchive.EntriesFromZipArchive(deploymentZipArchive));

                    var deploymentPineValue = Composition.FromTreeWithStringPath(deploymentTree);

                    var deploymentHashBase16 = CommonConversion.StringBase16(Composition.GetHash(deploymentPineValue));

                    logger.LogInformation("Got request to deploy app " + deploymentHashBase16);

                    processStoreWriter.StoreComponent(deploymentPineValue);

                    var deploymentEventValueInFile =
                        new ValueInFileStructure
                        {
                            HashBase16 = deploymentHashBase16
                        };

                    var compositionLogEvent =
                        CompositionLogRecordInFile.CompositionEvent.EventForDeployAppConfig(
                            appConfigValueInFile: deploymentEventValueInFile,
                            initElmAppState: initElmAppState);

                    await attemptContinueWithCompositionEventAndSendHttpResponse(compositionLogEvent);
                }

                Result<string, AdminInterface.ApplyFunctionOnDatabaseSuccess> applyFunctionOnDatabase(
                    AdminInterface.ApplyFunctionOnDatabaseRequest request)
                {
                    lock (avoidConcurrencyLock)
                    {
                        if (publicAppHost?.processLiveRepresentation is null)
                            return Result<string, AdminInterface.ApplyFunctionOnDatabaseSuccess>.err("No application deployed.");

                        return publicAppHost.processLiveRepresentation.ApplyFunctionOnMainBranch(storeWriter: processStoreWriter, request);
                    }
                }

                var apiRoutes = new[]
                {
                    new ApiRoute
                    (
                        path : PathApiGetDeployedAppConfig,
                        methods : ImmutableDictionary<string, Func<HttpContext, PublicHostConfiguration?, System.Threading.Tasks.Task>>.Empty
                        .Add("get", async (context, publicAppHost) =>
                        {
                            var appConfig = publicAppHost?.processLiveRepresentation?.lastAppConfig.appConfigComponent;

                            if (appConfig == null)
                            {
                                context.Response.StatusCode = 404;
                                await context.Response.WriteAsync("I did not find an app config in the history. Looks like no app was deployed so far.");
                                return;
                            }

                            var appConfigHashBase16 = CommonConversion.StringBase16(Composition.GetHash(appConfig));

                            var appConfigTreeResult = Composition.ParseAsTreeWithStringPath(appConfig);

                            var appConfigZipArchive =
                            appConfigTreeResult
                            .Unpack(
                                fromErr: error => throw   new Exception("Failed to parse as tree with string path"),
                                fromOk: appConfigTree =>
                                ZipArchive.ZipArchiveFromEntries(
                                    Composition.TreeToFlatDictionaryWithPathComparer(appConfigTree)));

                            context.Response.StatusCode = 200;
                            context.Response.Headers.ContentLength = appConfigZipArchive.LongLength;
                            context.Response.Headers.Add("Content-Disposition", new ContentDispositionHeaderValue("attachment") { FileName = appConfigHashBase16 + ".zip" }.ToString());
                            context.Response.Headers.Add("Content-Type", new MediaTypeHeaderValue("application/zip").ToString());

                            await context.Response.Body.WriteAsync(appConfigZipArchive);
                        })
                    ),
                    new ApiRoute
                    (
                        path : PathApiElmAppState,
                        methods : ImmutableDictionary<string, Func<HttpContext, PublicHostConfiguration?, System.Threading.Tasks.Task>>.Empty
                        .Add("get", async (context, publicAppHost) =>
                        {
                            if (publicAppHost == null)
                            {
                                context.Response.StatusCode = 400;
                                await context.Response.WriteAsync("Not possible because there is no app (state).");
                                return;
                            }

                            var processLiveRepresentation = publicAppHost?.processLiveRepresentation;

                            var components = new List<PineValue>();

                            var storeWriter = new DelegatingProcessStoreWriter
                            (
                                StoreComponentDelegate: components.Add,
                                StoreProvisionalReductionDelegate: _ => { },
                                AppendCompositionLogRecordDelegate: _ => throw new Exception("Unexpected use of interface.")
                            );

                            var reductionRecord =
                                processLiveRepresentation?.StoreReductionRecordForCurrentState(storeWriter).reductionRecord;

                            if (reductionRecord == null)
                            {
                                context.Response.StatusCode = 500;
                                await context.Response.WriteAsync("Not possible because there is no Elm app deployed at the moment.");
                                return;
                            }

                            var elmAppStateReductionHashBase16 = reductionRecord.elmAppState?.HashBase16;

                            var elmAppStateReductionComponent =
                                components.First(c => CommonConversion.StringBase16(Composition.GetHash(c)) == elmAppStateReductionHashBase16);

                            if(elmAppStateReductionComponent is not PineValue.BlobValue elmAppStateReductionComponentBlob)
                                throw   new Exception("elmAppStateReductionComponent is not a blob");

                            var elmAppStateReductionString =
                                Encoding.UTF8.GetString(elmAppStateReductionComponentBlob.Bytes.Span);

                            context.Response.StatusCode = 200;
                            context.Response.ContentType = "application/json";
                            await context.Response.WriteAsync(elmAppStateReductionString);
                        })
                        .Add("post", async (context, publicAppHost) =>
                        {
                            if (publicAppHost == null)
                            {
                                context.Response.StatusCode = 400;
                                await context.Response.WriteAsync("Not possible because there is no app (state).");
                                return;
                            }

                            var elmAppStateToSet = new StreamReader(context.Request.Body, Encoding.UTF8).ReadToEndAsync().Result;

                            var elmAppStateComponent = PineValue.Blob(Encoding.UTF8.GetBytes(elmAppStateToSet));

                            var appConfigValueInFile =
                                new ValueInFileStructure
                                {
                                    HashBase16 = CommonConversion.StringBase16(Composition.GetHash(elmAppStateComponent))
                                };

                            processStoreWriter.StoreComponent(elmAppStateComponent);

                            await attemptContinueWithCompositionEventAndSendHttpResponse(
                                new CompositionLogRecordInFile.CompositionEvent
                                {
                                    SetElmAppState = appConfigValueInFile
                                });
                        })
                    ),
                    new ApiRoute
                    (
                        path : PathApiDeployAndInitAppState,
                        methods : ImmutableDictionary<string, Func<HttpContext, PublicHostConfiguration?, System.Threading.Tasks.Task>>.Empty
                        .Add("post", async (context, publicAppHost) => await deployElmApp(initElmAppState: true))
                    ),
                    new ApiRoute
                    (
                        path : PathApiDeployAndMigrateAppState,
                        methods : ImmutableDictionary<string, Func<HttpContext, PublicHostConfiguration?, System.Threading.Tasks.Task>>.Empty
                        .Add("post", async (context, publicAppHost) => await deployElmApp(initElmAppState: false))
                    ),
                    new ApiRoute
                    (
                        path : PathApiReplaceProcessHistory,
                        methods : ImmutableDictionary<string, Func<HttpContext, PublicHostConfiguration?, System.Threading.Tasks.Task>>.Empty
                        .Add("post", async (context, publicAppHost) =>
                        {
                            var memoryStream = new MemoryStream();
                            context.Request.Body.CopyTo(memoryStream);

                            var historyZipArchive = memoryStream.ToArray();

                            var replacementFiles =
                                ZipArchive.EntriesFromZipArchive(historyZipArchive)
                                .Select(filePathAndContent =>
                                    (path: filePathAndContent.name.Split(new[] { '/', '\\' }).ToImmutableList()
                                    , filePathAndContent.content))
                                .ToImmutableList();

                            lock (avoidConcurrencyLock)
                            {
                                stopPublicApp();

                                foreach (var filePath in processStoreFileStore.ListFilesInDirectory(ImmutableList<string>.Empty).ToImmutableList())
                                    processStoreFileStore.DeleteFile(filePath);

                                foreach (var replacementFile in replacementFiles)
                                    processStoreFileStore.SetFileContent(replacementFile.path, replacementFile.content.ToArray());

                                startPublicApp();
                            }

                            context.Response.StatusCode = 200;
                            await context.Response.WriteAsync("Successfully replaced the process history.");
                        })
                    ),
                    new ApiRoute
                    (
                        path : PathApiApplyFunctionOnDatabase,
                        methods : ImmutableDictionary<string, Func<HttpContext, PublicHostConfiguration?, System.Threading.Tasks.Task>>.Empty
                        .Add("post", async (context, publicAppHost) =>
                        {
                            try
                            {
                                var applyFunctionRequest =
                                    await context.Request.ReadFromJsonAsync<AdminInterface.ApplyFunctionOnDatabaseRequest>();

                                var result = applyFunctionOnDatabase(applyFunctionRequest);

                                context.Response.StatusCode = result.Unpack(fromErr: _ => 400, fromOk: _ => 200);
                                await context.Response.WriteAsJsonAsync(result);
                            }
                            catch (Exception ex)
                            {
                                context.Response.StatusCode = 422;
                                await context.Response.WriteAsJsonAsync("Failed with runtime exception: " + ex.ToString());
                            }
                        })
                    ),
                };

                foreach (var apiRoute in apiRoutes)
                {
                    if (!context.Request.Path.Equals(new PathString(apiRoute.path)))
                        continue;

                    var matchingMethod =
                        apiRoute.methods
                        .FirstOrDefault(m => m.Key.ToUpperInvariant() == context.Request.Method.ToUpperInvariant());

                    if (matchingMethod.Value == null)
                    {
                        var supportedMethodsNames =
                            apiRoute.methods.Keys.Select(m => m.ToUpperInvariant()).ToList();

                        var guide =
                            HtmlFromLines(
                                "<h2>Method Not Allowed</h2>",
                                "",
                                context.Request.Path.ToString() +
                                " is a valid path, but the method " + context.Request.Method.ToUpperInvariant() +
                                " is not supported here.",
                                "Only following " +
                                (supportedMethodsNames.Count == 1 ? "method is" : "methods are") +
                                " supported here: " + string.Join(", ", supportedMethodsNames),
                                "", "",
                                ApiGuide);

                        context.Response.StatusCode = 405;
                        await context.Response.WriteAsync(HtmlDocument(guide));
                        return;
                    }

                    matchingMethod.Value?.Invoke(context, publicAppHost);
                    return;
                }

                if (context.Request.Path.StartsWithSegments(new PathString(PathApiRevertProcessTo),
                    out var revertToRemainingPath))
                {
                    if (!string.Equals(context.Request.Method, "post", StringComparison.InvariantCultureIgnoreCase))
                    {
                        context.Response.StatusCode = 405;
                        await context.Response.WriteAsync("Method not supported.");
                        return;
                    }

                    var processVersionId = revertToRemainingPath.ToString().Trim('/');

                    var processVersionCompositionRecord =
                        new ProcessStoreReaderInFileStore(processStoreFileStore)
                        .EnumerateSerializedCompositionLogRecordsReverse()
                        .FirstOrDefault(compositionEntry => CompositionLogRecordInFile.HashBase16FromCompositionRecord(compositionEntry) == processVersionId);

                    if (processVersionCompositionRecord == null)
                    {
                        context.Response.StatusCode = 404;
                        await context.Response.WriteAsync("Did not find process version '" + processVersionId + "'.");
                        return;
                    }

                    await attemptContinueWithCompositionEventAndSendHttpResponse(new CompositionLogRecordInFile.CompositionEvent
                    {
                        RevertProcessTo = new ValueInFileStructure { HashBase16 = processVersionId },
                    });
                    return;
                }

                TruncateProcessHistoryReport truncateProcessHistory(TimeSpan productionBlockDurationLimit)
                {
                    var beginTime = CommonConversion.TimeStringViewForReport(DateTimeOffset.UtcNow);

                    var totalStopwatch = System.Diagnostics.Stopwatch.StartNew();

                    var numbersOfThreadsToDeleteFiles = 4;

                    var filePathsInProcessStorePartitions =
                        processStoreFileStore.ListFiles()
                        .Select((s, i) => (s, i))
                        .GroupBy(x => x.i % numbersOfThreadsToDeleteFiles)
                        .Select(g => g.Select(x => x.s).ToImmutableList())
                        .ToImmutableList();

                    logger.LogInformation(
                        message: nameof(truncateProcessHistory) + ": Found {filePathCount} file paths to delete",
                        filePathsInProcessStorePartitions.Sum(partition => partition.Count));

                    lock (avoidConcurrencyLock)
                    {
                        var lockStopwatch = System.Diagnostics.Stopwatch.StartNew();

                        var storeReductionStopwatch = System.Diagnostics.Stopwatch.StartNew();

                        var storeReductionReport =
                            publicAppHost?.processLiveRepresentation?.StoreReductionRecordForCurrentState(processStoreWriter).report;

                        storeReductionStopwatch.Stop();

                        logger.LogInformation(
                            message: nameof(truncateProcessHistory) + ": Stored reduction in {storeReductionDurationMs} ms",
                            storeReductionStopwatch.ElapsedMilliseconds);

                        var getFilesForRestoreStopwatch = System.Diagnostics.Stopwatch.StartNew();

                        var filesForRestore =
                            PersistentProcessLiveRepresentation.GetFilesForRestoreProcess(
                                processStoreFileStore).files
                            .Select(filePathAndContent => filePathAndContent.Key)
                            .ToImmutableHashSet(EnumerableExtension.EqualityComparer<IReadOnlyList<string>>());

                        getFilesForRestoreStopwatch.Stop();

                        var deleteFilesStopwatch = System.Diagnostics.Stopwatch.StartNew();

                        var partitionsTasks =
                            filePathsInProcessStorePartitions
                            .Select(partitionFilePaths => System.Threading.Tasks.Task.Run(() =>
                            {
                                int partitionDeletedFilesCount = 0;

                                foreach (var filePath in partitionFilePaths)
                                {
                                    if (filesForRestore.Contains(filePath))
                                        continue;

                                    if (productionBlockDurationLimit < lockStopwatch.Elapsed)
                                        break;

                                    processStoreFileStore.DeleteFile(filePath);
                                    ++partitionDeletedFilesCount;
                                }

                                return partitionDeletedFilesCount;
                            }))
                            .ToImmutableList();

                        var totalDeletedFilesCount = partitionsTasks.Sum(task => task.Result);

                        deleteFilesStopwatch.Stop();

                        logger.LogInformation(
                            message: nameof(truncateProcessHistory) + ": Deleted {totalDeletedFilesCount} files in {storeReductionDurationMs} ms",
                            totalDeletedFilesCount,
                            deleteFilesStopwatch.ElapsedMilliseconds);

                        return new TruncateProcessHistoryReport
                        (
                            beginTime: beginTime,
                            filesForRestoreCount: filesForRestore.Count,
                            discoveredFilesCount: filePathsInProcessStorePartitions.Sum(partition => partition.Count),
                            deletedFilesCount: totalDeletedFilesCount,
                            storeReductionTimeSpentMilli: (int)storeReductionStopwatch.ElapsedMilliseconds,
                            storeReductionReport: storeReductionReport,
                            getFilesForRestoreTimeSpentMilli: (int)getFilesForRestoreStopwatch.ElapsedMilliseconds,
                            deleteFilesTimeSpentMilli: (int)deleteFilesStopwatch.ElapsedMilliseconds,
                            lockedTimeSpentMilli: (int)lockStopwatch.ElapsedMilliseconds,
                            totalTimeSpentMilli: (int)totalStopwatch.ElapsedMilliseconds
                        );
                    }
                }

                if (context.Request.Path.Equals(new PathString(PathApiTruncateProcessHistory)))
                {
                    var truncateResult = truncateProcessHistory(productionBlockDurationLimit: TimeSpan.FromMinutes(1));

                    context.Response.StatusCode = 200;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(truncateResult));
                    return;
                }

                {
                    if (context.Request.Path.StartsWithSegments(
                        new PathString(PathApiProcessHistoryFileStoreGetFileContent), out var remainingPathString))
                    {
                        if (!string.Equals(context.Request.Method, "get", StringComparison.InvariantCultureIgnoreCase))
                        {
                            context.Response.StatusCode = 405;
                            await context.Response.WriteAsync("Method not supported.");
                            return;
                        }

                        var filePathInStore =
                            remainingPathString.ToString().Trim('/').Split('/').ToImmutableList();

                        var fileContent = processStoreFileStore.GetFileContent(filePathInStore);

                        if (fileContent == null)
                        {
                            context.Response.StatusCode = 404;
                            await context.Response.WriteAsync("No file at '" + string.Join("/", filePathInStore) + "'.");
                            return;
                        }

                        context.Response.StatusCode = 200;
                        context.Response.ContentType = "application/octet-stream";
                        await context.Response.Body.WriteAsync(fileContent as byte[] ?? fileContent.ToArray());
                        return;
                    }
                }

                {
                    if (context.Request.Path.StartsWithSegments(
                        new PathString(PathApiProcessHistoryFileStoreListFilesInDirectory), out var remainingPathString))
                    {
                        if (!string.Equals(context.Request.Method, "get", StringComparison.InvariantCultureIgnoreCase))
                        {
                            context.Response.StatusCode = 405;
                            await context.Response.WriteAsync("Method not supported.");
                            return;
                        }

                        var filePathInStore =
                            remainingPathString.ToString().Trim('/').Split('/').ToImmutableList();

                        var filesPaths = processStoreFileStore.ListFilesInDirectory(filePathInStore);

                        var filesPathsList =
                            string.Join('\n', filesPaths.Select(path => string.Join('/', path)));

                        context.Response.StatusCode = 200;
                        context.Response.ContentType = "application/octet-stream";
                        await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(filesPathsList));
                        return;
                    }
                }

                (int statusCode, AttemptContinueWithCompositionEventReport responseReport) attemptContinueWithCompositionEvent(
                    CompositionLogRecordInFile.CompositionEvent compositionLogEvent)
                {
                    lock (avoidConcurrencyLock)
                    {
                        var storeReductionStopwatch = System.Diagnostics.Stopwatch.StartNew();

                        var storeReductionReport =
                            publicAppHost?.processLiveRepresentation?.StoreReductionRecordForCurrentState(processStoreWriter).report;

                        storeReductionStopwatch.Stop();

                        var (statusCode, report) =
                            AttemptContinueWithCompositionEventAndCommit(
                                compositionLogEvent,
                                processStoreFileStore,
                                testContinueLogger: logEntry => logger.LogInformation(logEntry));

                        report = report with
                        {
                            storeReductionTimeSpentMilli = (int)storeReductionStopwatch.ElapsedMilliseconds,
                            storeReductionReport = storeReductionReport
                        };

                        startPublicApp();

                        return (statusCode, report);
                    }
                }

                async System.Threading.Tasks.Task attemptContinueWithCompositionEventAndSendHttpResponse(
                    CompositionLogRecordInFile.CompositionEvent compositionLogEvent,
                    ILogger? logger = null)
                {
                    logger?.LogInformation(
                        "Begin attempt to continue with composition event: " +
                        System.Text.Json.JsonSerializer.Serialize(compositionLogEvent));

                    var (statusCode, attemptReport) = attemptContinueWithCompositionEvent(compositionLogEvent);

                    var responseBodyString = System.Text.Json.JsonSerializer.Serialize(attemptReport);

                    context.Response.StatusCode = statusCode;
                    await context.Response.WriteAsync(responseBodyString);
                }

                if (context.Request.Path.Equals(PathString.Empty) || context.Request.Path.Equals(new PathString("/")))
                {
                    var httpApiGuide =
                        HtmlFromLines(
                            "<h3>HTTP APIs</h3>\n" +
                            HtmlFromLines(apiRoutes.Select(HtmlToDescribeApiRoute).ToArray())
                        );

                    context.Response.StatusCode = 200;
                    await context.Response.WriteAsync(
                        HtmlDocument(
                            HtmlFromLines(
                                "Welcome to the Elm-Time admin interface version " + Program.AppVersionId + ".",
                                httpApiGuide,
                                "",
                                ApiGuide)));
                    return;
                }

                context.Response.StatusCode = 404;
                await context.Response.WriteAsync("Not Found");
                return;
            });
    }

    static string ApiGuide =>
        HtmlFromLines(
            "The easiest way to use the APIs is via the command-line interface in the elm-time executable file.",
            "To learn about the admin interface and how to deploy an app, see  " + LinkHtmlElementFromUrl(LinkToGuideUrl)
        );

    static string LinkToGuideUrl => "https://github.com/elm-time/elm-time/blob/main/guide/how-to-configure-and-deploy-an-elm-backend-app.md";

    static string LinkHtmlElementFromUrl(string url) =>
        "<a href='" + url + "'>" + url + "</a>";

    static string HtmlFromLines(params string[] lines) =>
        string.Join("<br>\n", lines);

    static string HtmlToDescribeApiRoute(ApiRoute apiRoute) =>
        LinkHtmlElementFromUrl(apiRoute.path) +
        " [ " + string.Join(", ", apiRoute.methods.Select(m => m.Key.ToUpperInvariant())) + " ]";

    record ApiRoute(
        string path,
        ImmutableDictionary<string, Func<HttpContext, PublicHostConfiguration?, System.Threading.Tasks.Task>> methods);

    static public string HtmlDocument(string body) =>
        string.Join("\n",
        new[]
        {
            "<html>",
            "<body>",
            body,
            "</body>",
            "</html>"
        });

    static public (int statusCode, AttemptContinueWithCompositionEventReport responseReport) AttemptContinueWithCompositionEventAndCommit(
        CompositionLogRecordInFile.CompositionEvent compositionLogEvent,
        IFileStore processStoreFileStore,
        Action<string>? testContinueLogger = null)
    {
        var beginTime = CommonConversion.TimeStringViewForReport(DateTimeOffset.UtcNow);

        var totalStopwatch = System.Diagnostics.Stopwatch.StartNew();

        var testContinueResult = PersistentProcessLiveRepresentation.TestContinueWithCompositionEvent(
            compositionLogEvent: compositionLogEvent,
            fileStoreReader: processStoreFileStore,
            logger: testContinueLogger);

        var projectionResult = IProcessStoreReader.ProjectFileStoreReaderForAppendedCompositionLogEvent(
            originalFileStore: processStoreFileStore,
            compositionLogEvent: compositionLogEvent);

        return
            testContinueResult
            .Unpack(
                fromErr: error =>
                (statusCode: 400, new AttemptContinueWithCompositionEventReport
                (
                    beginTime: beginTime,
                    compositionEvent: compositionLogEvent,
                    storeReductionReport: null,
                    storeReductionTimeSpentMilli: null,
                    totalTimeSpentMilli: (int)totalStopwatch.ElapsedMilliseconds,
                    result: Result<string, string>.err(error)
                )),
                fromOk: testContinueOk =>
                {
                    foreach (var projectedFilePathAndContent in testContinueOk.projectedFiles)
                        processStoreFileStore.SetFileContent(
                            projectedFilePathAndContent.filePath, projectedFilePathAndContent.fileContent);

                    return (statusCode: 200, new AttemptContinueWithCompositionEventReport
                    (
                        beginTime: beginTime,
                        compositionEvent: compositionLogEvent,
                        storeReductionReport: null,
                        storeReductionTimeSpentMilli: null,
                        totalTimeSpentMilli: (int)totalStopwatch.ElapsedMilliseconds,
                        result: Result<string, string>.ok("Successfully applied this composition event to the process.")
                    ));
                });
    }
}

public record AttemptContinueWithCompositionEventReport(
    string beginTime,
    CompositionLogRecordInFile.CompositionEvent compositionEvent,
    StoreProvisionalReductionReport? storeReductionReport,
    int? storeReductionTimeSpentMilli,
    int totalTimeSpentMilli,
    Result<string, string> result);

public record TruncateProcessHistoryReport(
    string beginTime,
    int filesForRestoreCount,
    int discoveredFilesCount,
    int deletedFilesCount,
    int lockedTimeSpentMilli,
    int totalTimeSpentMilli,
    int storeReductionTimeSpentMilli,
    StoreProvisionalReductionReport? storeReductionReport,
    int getFilesForRestoreTimeSpentMilli,
    int deleteFilesTimeSpentMilli);