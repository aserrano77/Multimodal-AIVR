using System;
using System.Collections.Generic;
using UnityEngine;

namespace Autonomy.Domain
{
    public enum SpawnGenerationMode
    {
        RandomBalanced,
        DeterministicDebug
    }

    public sealed class ExperimentalRoundSnapshot
    {
        public ExperimentalRoundSnapshot(
            int totalBoxes,
            int depositedBoxes,
            bool roundActive,
            bool roundFinished,
            Transform activeRoundContainer,
            IReadOnlyCollection<Component> countedBoxes)
        {
            TotalBoxes = totalBoxes;
            DepositedBoxes = depositedBoxes;
            RoundActive = roundActive;
            RoundFinished = roundFinished;
            ActiveRoundContainer = activeRoundContainer;
            CountedBoxes = countedBoxes ?? Array.Empty<Component>();
        }

        public int TotalBoxes { get; }
        public int DepositedBoxes { get; }
        public bool RoundActive { get; }
        public bool RoundFinished { get; }
        public Transform ActiveRoundContainer { get; }
        public IReadOnlyCollection<Component> CountedBoxes { get; }
    }

    public interface IExperimentalRoundLifecycle
    {
        event Action<ExperimentalRoundSnapshot> RoundStarted;
        event Action<Component, ExperimentalRoundSnapshot> CorrectDepositRegistered;
        event Action<ExperimentalRoundSnapshot> RoundCompleted;
        event Action<ExperimentalRoundSnapshot> RoundReset;

        ExperimentalRoundSnapshot CurrentRound { get; }
        bool IsInActiveRound(Component component);
    }

    public interface IExperimentalRoundDepositRegistrar
    {
        bool TryRegisterCorrectDeposit(Component box, out string rejectionReason);
    }

    public static class SpawnSequenceBuilder
    {
        public static List<int> Build(
            int spawnCount,
            int prefabCount,
            SpawnGenerationMode mode,
            IReadOnlyList<int> deterministicPrefabIndices = null,
            Action<IList<int>> shuffle = null)
        {
            var result = new List<int>();
            if (spawnCount <= 0 || prefabCount <= 0)
            {
                return result;
            }

            for (int i = 0; i < spawnCount; i++)
            {
                int prefabIndex = ResolvePrefabIndex(i, prefabCount, mode, deterministicPrefabIndices);
                result.Add(prefabIndex);
            }

            if (mode == SpawnGenerationMode.RandomBalanced)
            {
                shuffle?.Invoke(result);
            }

            return result;
        }

        private static int ResolvePrefabIndex(
            int spawnIndex,
            int prefabCount,
            SpawnGenerationMode mode,
            IReadOnlyList<int> deterministicPrefabIndices)
        {
            if (mode == SpawnGenerationMode.DeterministicDebug &&
                deterministicPrefabIndices != null &&
                deterministicPrefabIndices.Count > 0)
            {
                return Mathf.Abs(deterministicPrefabIndices[spawnIndex % deterministicPrefabIndices.Count]) % prefabCount;
            }

            return spawnIndex % prefabCount;
        }
    }
}
