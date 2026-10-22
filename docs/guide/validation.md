# Validation: M/M/1

`Assets/Scenes/Validation/MM1.unity` is the textbook single-server queue, set up so that its results can be checked
against queueing theory: Poisson arrivals (Source, exponential gaps, λ = 0.8 per minute) → an unlimited Queue → one
server (Multi-Server, capacity 1, exponential service, μ = 1 per minute) → Exit (a tally `time_in_system = age`). The
clock is in minutes, with a horizon of 20 000 min, a warm-up of 2 000 min and a fixed seed.

With ρ = λ/μ = 0.8, theory gives Wq = ρ/(μ − λ) = 4, Lq = ρ²/(1 − ρ) = 3.2, W = 1/(μ − λ) = 5, L = ρ/(1 − ρ) = 4.
Ten replications (seeds 1–10, 18 000 observed minutes each):

| Measure | Metric path | Simulated (mean ± 95 % half-width) | Theory |
|---|---|---|---|
| Wq — waiting time in the queue | `Queue.stay_avg` | 3.96 ± 0.17 | 4 |
| Lq — number in the queue | `Queue.content_avg` | 3.18 ± 0.15 | 3.2 |
| W — time in the system | `Exit.tally:time_in_system:mean` | 4.95 ± 0.17 | 5 |
| L — number in the system | derived `Lq + rho` | 3.98 ± 0.15 | 4 |
| ρ — server utilisation | `Server.utilization` | 0.801 ± 0.006 | 0.8 |

Every theoretical value is inside its interval. To repeat it through an agent:

```
simulean_run_headless replications=10 metrics=Wq=Queue.stay_avg;Lq=Queue.content_avg;W=Exit.tally:time_in_system:mean;rho=Server.utilization derived=L=Lq+rho
```

The EditMode test `MM1ValidationTests` runs the scene the same way (fewer replications) and checks the results against
the theory; the engine suites check an M/M/1 with ρ = 0.5 (`StandaloneTests/bugfixtests`).
