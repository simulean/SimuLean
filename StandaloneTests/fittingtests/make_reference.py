"""Reference values for fittingtests, from scipy / numpy. Run once: python make_reference.py
Writes reference.tsv: fixed samples, scipy's maximum-likelihood fits, Kolmogorov-Smirnov / Anderson-Darling statistics
of those fits, and digamma / trigamma values. Regenerate only when the samples change."""
import numpy as np
from scipy import stats, special

rng = np.random.default_rng(20261007)
samples = {
    'exp': rng.exponential(scale=8.0, size=400),
    'norm': rng.normal(50.0, 6.0, size=300),
    'lognorm': rng.lognormal(mean=1.2, sigma=0.4, size=350),
    'gamma': rng.gamma(shape=2.5, scale=3.0, size=500),
    'weibull': 4.0 * rng.weibull(1.7, size=450),
    'uniform': rng.uniform(3.0, 9.0, size=200),
    'poisson': rng.poisson(4.2, size=300).astype(float),
}

rows = []
for name, x in samples.items():
    rows.append(('sample', name, ','.join(repr(float(v)) for v in x)))

def fit_rows(name, family, params, cdf):
    x = np.sort(samples[name])
    rows.append(('fit', name, family, ','.join(repr(float(p)) for p in params)))
    d, p = stats.kstest(x, cdf)
    n = len(x)
    f = np.clip(cdf(x), 1e-300, 1 - 1e-16)
    i = np.arange(1, n + 1)
    a2 = -n - np.sum((2 * i - 1) * (np.log(f) + np.log(1 - f[::-1]))) / n
    rows.append(('gof', name, family, repr(float(d)), repr(float(a2))))

x = samples['exp']; rate = 1 / x.mean()
fit_rows('exp', 'Exponential', [rate], lambda v: stats.expon.cdf(v, scale=1 / rate))
x = samples['norm']; m, s = stats.norm.fit(x)
fit_rows('norm', 'Normal', [m, s], lambda v: stats.norm.cdf(v, m, s))
x = samples['lognorm']; sh, loc, sc = stats.lognorm.fit(x, floc=0)
fit_rows('lognorm', 'LogNormal', [np.log(sc), sh], lambda v: stats.lognorm.cdf(v, sh, scale=sc))
x = samples['gamma']; a, loc, sc = stats.gamma.fit(x, floc=0, method='MLE')
fit_rows('gamma', 'Gamma', [a, sc], lambda v: stats.gamma.cdf(v, a, scale=sc))
x = samples['weibull']; c, loc, sc = stats.weibull_min.fit(x, floc=0)
fit_rows('weibull', 'Weibull', [c, sc], lambda v: stats.weibull_min.cdf(v, c, scale=sc))
x = samples['uniform']; g = (x.max() - x.min()) / (len(x) - 1); lo, hi = x.min() - g, x.max() + g   # unbiased endpoints
fit_rows('uniform', 'Uniform', [lo, hi], lambda v: stats.uniform.cdf(v, lo, hi - lo))
x = samples['poisson']; lam = x.mean()
rows.append(('fit', 'poisson', 'Poisson', repr(float(lam))))

for v in [0.1, 0.5, 1.0, 1.5, 2.5, 5.0, 7.3, 12.0, 50.0, 1000.0]:
    rows.append(('digamma', repr(v), repr(float(special.digamma(v)))))
    rows.append(('trigamma', repr(v), repr(float(special.polygamma(1, v)))))

with open('reference.tsv', 'w', newline='\n') as f:
    f.write('# fittingtests reference (scipy %s, numpy %s); see make_reference.py\n' % (__import__('scipy').__version__, np.__version__))
    for r in rows:
        f.write('\t'.join(r) + '\n')
print(len(rows), 'rows')
