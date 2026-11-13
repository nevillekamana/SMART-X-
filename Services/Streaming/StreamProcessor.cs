namespace SmartX.Services.Streaming;

/// <summary>
/// Background worker that drains the ingestion queues. Every tick it takes ALL waiting critical packets first,
/// then a bounded batch of standard packets, so a critical alert never waits behind routine telemetry.
/// It also runs the disconnect watchdog and samples throughput once a second.
/// </summary>
public class StreamProcessor : BackgroundService
{
    private readonly IngestionPipeline _pipeline;
    private readonly ILogger<StreamProcessor> _log;
    private readonly TimeSpan _disconnectAfter;
    private const int StandardPerTick = 25;

    public StreamProcessor(IngestionPipeline pipeline, IConfiguration config, ILogger<StreamProcessor> log)
    {
        _pipeline = pipeline;
        _log = log;
        _disconnectAfter = TimeSpan.FromSeconds(config.GetValue("Streaming:DisconnectAfterSeconds", 30));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var lastSample = DateTime.UtcNow;
        var lastWatchdog = DateTime.UtcNow;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_pipeline.Paused)
                {
                    var batch = _pipeline.TakeBatch(StandardPerTick);
                    foreach (var packet in batch)
                    {
                        try { _pipeline.Process(packet); }
                        catch (Exception ex) { _log.LogError(ex, "Failed to process packet {Seq}", packet.Sequence); }
                    }
                }

                var now = DateTime.UtcNow;
                if (now - lastSample >= TimeSpan.FromSeconds(1))
                {
                    _pipeline.SampleThroughput();
                    lastSample = now;
                }
                if (!_pipeline.Paused && now - lastWatchdog >= TimeSpan.FromSeconds(2))
                {
                    _pipeline.CheckDisconnects(_disconnectAfter);
                    lastWatchdog = now;
                }

                await Task.Delay(50, stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogError(ex, "Stream processor loop error");
                await Task.Delay(500, stoppingToken);
            }
        }
    }
}
