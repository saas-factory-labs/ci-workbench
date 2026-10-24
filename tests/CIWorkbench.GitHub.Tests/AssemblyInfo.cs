// ActivitySource and Meter are process-wide: serialize this assembly so telemetry captured by one test never includes another's.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
