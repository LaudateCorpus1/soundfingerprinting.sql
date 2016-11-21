﻿namespace SoundFingerprinting.SQL.Tests.Integration
{
    using System;
    using System.IO;
    using System.Linq;
    using System.Transactions;

    using Microsoft.VisualStudio.TestTools.UnitTesting;

    using SoundFingerprinting.Audio;
    using SoundFingerprinting.Audio.Bass;
    using SoundFingerprinting.Audio.NAudio;
    using SoundFingerprinting.Builder;
    using SoundFingerprinting.Command;
    using SoundFingerprinting.Configuration;
    using SoundFingerprinting.DAO.Data;
    using SoundFingerprinting.Strides;

    [TestClass]
    public class FingerprintCommandBuilderIntTest : AbstractIntegrationTest
    {
        private static readonly Random Rand = new Random();

        private readonly ModelService modelService;
        private readonly IFingerprintCommandBuilder fingerprintCommandBuilder;
        private readonly QueryFingerprintService queryFingerprintService;
        private readonly BassAudioService bassAudioService;
        private readonly BassWaveFileUtility bassWaveFileUtility;
        private readonly NAudioService nAudioService;

        private TransactionScope transactionPerTestScope;

        public FingerprintCommandBuilderIntTest()
        {
            bassAudioService = new BassAudioService();
            this.nAudioService = new NAudioService();
            modelService = new SqlModelService();
            bassWaveFileUtility = new BassWaveFileUtility();
            fingerprintCommandBuilder = new FingerprintCommandBuilder();
            queryFingerprintService = new QueryFingerprintService();
        }

        [TestInitialize]
        public void SetUp()
        {
            transactionPerTestScope = new TransactionScope();
        }

        [TestCleanup]
        public void TearDown()
        {
            transactionPerTestScope.Dispose();
        }

        [TestMethod]
        public void CreateFingerprintsFromDefaultFileAndAssertNumberOfFingerprints()
        {
            const int StaticStride = 5115;
            var tagService = new BassTagService();

            var audioFingerprintingUnit = fingerprintCommandBuilder.BuildFingerprintCommand()
                                        .From(PathToMp3)
                                        .WithFingerprintConfig(config => { config.SpectrogramConfig.Stride = new IncrementalStaticStride(StaticStride, config.SamplesPerFingerprint); })
                                        .UsingServices(bassAudioService);
                                    
            double seconds = tagService.GetTagInfo(PathToMp3).Duration;
            int samples = (int)(seconds * audioFingerprintingUnit.FingerprintConfiguration.SampleRate);
            int expectedFingerprints = (samples / StaticStride) - 1;

            var fingerprints = ((FingerprintCommand)audioFingerprintingUnit).Fingerprint().Result;

            Assert.AreEqual(expectedFingerprints, fingerprints.Count);
        }

        [TestMethod]
        public void CreateFingerprintsInsertThenQueryAndGetTheRightResult()
        {
            const int SecondsToProcess = 10;
            const int StartAtSecond = 30;
            var tagService = new BassTagService();
            var info = tagService.GetTagInfo(PathToMp3);
            var track = new TrackData(info.ISRC, info.Artist, info.Title, info.Album, info.Year, (int)info.Duration);
            var trackReference = modelService.InsertTrack(track);

            var hashDatas = fingerprintCommandBuilder
                                            .BuildFingerprintCommand()
                                            .From(PathToMp3, SecondsToProcess, StartAtSecond)
                                            .UsingServices(bassAudioService)
                                            .Hash()
                                            .Result;

            modelService.InsertHashDataForTrack(hashDatas, trackReference);

            var queryResult = queryFingerprintService.Query(hashDatas, new DefaultQueryConfiguration(), modelService);

            Assert.IsTrue(queryResult.ContainsMatches);
            Assert.AreEqual(1, queryResult.ResultEntries.Count());
            Assert.AreEqual(trackReference, queryResult.BestMatch.Track.TrackReference);
        }

        [TestMethod]
        public void CreateFingerprintsFromFileAndFromAudioSamplesAndGetTheSameResultTest()
        {
            const int SecondsToProcess = 20;
            const int StartAtSecond = 15;
            var audioService = new BassAudioService();

            AudioSamples samples = audioService.ReadMonoSamplesFromFile(PathToMp3, SampleRate, SecondsToProcess, StartAtSecond);

            var hashDatasFromFile = fingerprintCommandBuilder
                                        .BuildFingerprintCommand()
                                        .From(PathToMp3, SecondsToProcess, StartAtSecond)
                                        .UsingServices(bassAudioService)
                                        .Hash()
                                        .Result;

            var hashDatasFromSamples = fingerprintCommandBuilder
                                        .BuildFingerprintCommand()
                                        .From(samples)
                                        .UsingServices(bassAudioService)
                                        .Hash()
                                        .Result;

            AssertHashDatasAreTheSame(hashDatasFromFile, hashDatasFromSamples);
        }

        [TestMethod]
        public void CompareFingerprintsCreatedByDifferentProxiesTest()
        {
            var naudioFingerprints = ((FingerprintCommand)fingerprintCommandBuilder.BuildFingerprintCommand()
                                                        .From(PathToMp3)
                                                        .UsingServices(nAudioService))
                                                        .Fingerprint()
                                                        .Result;

            var bassFingerprints = ((FingerprintCommand)fingerprintCommandBuilder.BuildFingerprintCommand()
                                                 .From(PathToMp3)
                                                 .UsingServices(bassAudioService))
                                                 .Fingerprint()
                                                 .Result;
            int unmatchedItems = 0;
            int totalmatches = 0;

            Assert.AreEqual(bassFingerprints.Count, naudioFingerprints.Count);
            for (int i = 0; i < naudioFingerprints.Count; i++)
            {
                for (int j = 0; j < naudioFingerprints[i].Signature.Length; j++)
                {
                    if (naudioFingerprints[i].Signature[j] != bassFingerprints[i].Signature[j])
                    {
                        unmatchedItems++;
                    }

                    totalmatches++;
                }
            }

            Assert.AreEqual(true, (float)unmatchedItems / totalmatches < 0.04, "Rate: " + ((float)unmatchedItems / totalmatches));
            Assert.AreEqual(bassFingerprints.Count, naudioFingerprints.Count);
        }

        [TestMethod]
        public void CheckFingerprintCreationAlgorithmTest()
        {
            string tempFile = Path.GetTempPath() + DateTime.Now.Ticks + ".wav";
            RecodeFileToWaveFile(tempFile);
            long fileSize = new FileInfo(tempFile).Length;

            var list = fingerprintCommandBuilder.BuildFingerprintCommand()
                                      .From(PathToMp3)
                                      .WithFingerprintConfig(customConfiguration => customConfiguration.SpectrogramConfig.Stride = new StaticStride(0, 0))
                                      .UsingServices(bassAudioService)
                                      .Hash()
                                      .Result;

            long expected = fileSize / (8192 * 4); // One fingerprint corresponds to a granularity of 8192 samples which is 16384 bytes
            Assert.AreEqual(expected, list.Count);
            File.Delete(tempFile);
        }
        
        [TestMethod]
        public void CreateFingerprintsWithTheSameFingerprintCommandTest()
        {
            const int SecondsToProcess = 20;
            const int StartAtSecond = 15;

            var fingerprintCommand = fingerprintCommandBuilder
                                            .BuildFingerprintCommand()
                                            .From(PathToMp3, SecondsToProcess, StartAtSecond)
                                            .UsingServices(bassAudioService);
            
            var firstHashDatas = fingerprintCommand.Hash().Result;
            var secondHashDatas = fingerprintCommand.Hash().Result;

            AssertHashDatasAreTheSame(firstHashDatas, secondHashDatas);
        }

        [TestMethod]
        public void CreateFingerprintFromSamplesWhichAreExactlyEqualToMinimumLength()
        {
            DefaultFingerprintConfiguration config = new DefaultFingerprintConfiguration();

            AudioSamples samples = GenerateRandomAudioSamples(config.SamplesPerFingerprint + config.SpectrogramConfig.WdftSize);

            var hash = fingerprintCommandBuilder.BuildFingerprintCommand()
                                                .From(samples)
                                                .UsingServices(bassAudioService)
                                                .Hash()
                                                .Result;
            Assert.AreEqual(1, hash.Count);
        }

        private void RecodeFileToWaveFile(string tempFile)
        {
            var samples = bassAudioService.ReadMonoSamplesFromFile(PathToMp3, 5512);
            bassWaveFileUtility.WriteSamplesToFile(samples.Samples, 5512, tempFile);
        }

        private AudioSamples GenerateRandomAudioSamples(int length)
        {
            return new AudioSamples
            {
                Duration = length,
                Origin = string.Empty,
                SampleRate = 5512,
                Samples = GenerateRandomFloatArray(length)
            };
        }

        private float[] GenerateRandomFloatArray(int length)
        {
            float[] result = new float[length];
            for (int i = 0; i < length; i++)
            {
                result[i] = (float)Rand.NextDouble() * 32767;
            }

            return result;
        }
    }

}
