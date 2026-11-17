import math
from itertools import combinations

import pandas as pd
import matplotlib.pyplot as plt
import seaborn as sns

STAGES = ["synthesis", "pcr", "decay", "sequencing"]
TARGET = "1_minus_recovery_rate"


def build_value_table(data, stages, target_col=TARGET):
    return data.groupby(stages)[target_col].mean().to_dict()


def coalition_value(coalition, stages, table):
   # 0 is without coalition, 1 with
    key = tuple(0 if s in coalition else 1 for s in stages)
    try:
        return table[key]
    except KeyError:
        raise ValueError(f"Missing row {set(coalition)}")


def shapley(stages, data, target_col=TARGET):
    n = len(stages)
    table = build_value_table(data, stages, target_col)
    results = {stage: 0.0 for stage in stages}

    for stage in stages:

        others = []
        for s in stages:
            if (s!= stage): 
                others.append(s)

        for r in range(n):
            for comb in combinations(others, r):
                without = frozenset(comb)
                with_stage = without | {stage}

                marginal = (
                    coalition_value(with_stage, stages, table)
                    - coalition_value(without, stages, table)
                ) #how big is the impact of the addition of the "player"
                weight = (
                    math.factorial(r) * math.factorial(n - r - 1) / math.factorial(n)
                ) #weight
                results[stage] += weight * marginal

    return results


df = pd.read_csv("sample_data.csv")

rows = []
for codec, codec_data in df.groupby("codec"):
    sh = shapley(STAGES, codec_data)          
    for stage, value in sh.items():
        rows.append({"codec": codec, "stage": stage, "value": value})

df_results = pd.DataFrame(rows)

plt.figure(figsize=(10, 6))
sns.barplot(data=df_results, x="codec", y="value", hue="stage")
plt.ylabel("Shapley Value")
plt.tight_layout()
plt.show()

