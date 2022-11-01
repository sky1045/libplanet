using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Bencodex;
using Bencodex.Types;
using Libplanet.Blocks;
using Libplanet.Consensus;
using Libplanet.Crypto;
using Libplanet.Net.Consensus;
using Libplanet.Net.Messages;
using Libplanet.Tests;
using Libplanet.Tests.Common.Action;
using Nito.AsyncEx;
using Serilog;
using Xunit;
using Xunit.Abstractions;

namespace Libplanet.Net.Tests.Consensus.ConsensusContext
{
    public class ConsensusContextTest
    {
        private const int Timeout = 30000;
        private readonly ILogger _logger;

        public ConsensusContextTest(ITestOutputHelper output)
        {
            const string outputTemplate =
                "{Timestamp:HH:mm:ss:ffffffZ} - {Message} {Exception}";
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Verbose()
                .WriteTo.TestOutput(output, outputTemplate: outputTemplate)
                .CreateLogger()
                .ForContext<ConsensusContextTest>();

            _logger = Log.ForContext<ConsensusContextTest>();
        }

        [Fact(Timeout = Timeout)]
        public async void NewHeightIncreasing()
        {
            var validators = new List<PublicKey>
            {
                TestUtils.Peer0Priv.PublicKey, TestUtils.Peer1Priv.PublicKey,
            };
            var proposalMessageSent = new AsyncAutoResetEvent();
            var (_, blockChain, consensusContext) = TestUtils.CreateDummyConsensusContext(
                TimeSpan.FromSeconds(1),
                TestUtils.Policy,
                TestUtils.Peer1Priv,
                validators,
                consensusMessageSent: CatchProposal);
            BlockHash? proposedHash = null;

            AsyncAutoResetEvent stepChangedToEndCommit = new AsyncAutoResetEvent();
            consensusContext.StateChanged += (_, eventArgs) =>
            {
                if (eventArgs.Height == 1 && eventArgs.Step == Step.EndCommit)
                {
                    stepChangedToEndCommit.Set();
                }
            };
            void CatchProposal(object? sender, ConsensusMsg? message)
            {
                if (message is ConsensusProposalMsg proposal)
                {
                    proposedHash = proposal.Proposal.BlockHash;
                    proposalMessageSent.Set();
                }
            }

            // The given height is equal to the consensus context's height.
            Assert.Throws<InvalidHeightIncreasingException>(
                () => consensusContext.NewHeight(blockChain.Tip.Index));
            // The given height is not the same tip's index + 1.
            Assert.Throws<InvalidHeightIncreasingException>(
                () => consensusContext.NewHeight(blockChain.Tip.Index + 2));

            consensusContext.NewHeight(blockChain.Tip.Index + 1);
            await proposalMessageSent.WaitAsync();
            Assert.NotNull(proposedHash);

            consensusContext.HandleMessage(
                new ConsensusPreVoteMsg(
                    TestUtils.CreateVote(
                        TestUtils.Peer0Priv, 1, hash: proposedHash, flag: VoteFlag.PreVote)));
            consensusContext.HandleMessage(
                new ConsensusPreCommitMsg(
                    TestUtils.CreateVote(
                        TestUtils.Peer0Priv, 1, hash: proposedHash, flag: VoteFlag.PreCommit)));

            // Waiting for commit.
            await stepChangedToEndCommit.WaitAsync();
            Assert.Equal(1, blockChain.Tip.Index);

            // Next NewHeight is not called yet.
            Assert.Equal(1, consensusContext.Height);
            Assert.Equal(0, consensusContext.Round);
        }

        [Fact(Timeout = Timeout)]
        public void Ctor()
        {
            var validators = new List<PublicKey>()
            {
                TestUtils.Peer0Priv.PublicKey, TestUtils.Peer1Priv.PublicKey,
            };

            var (_, _, consensusContext) = TestUtils.CreateDummyConsensusContext(
                TimeSpan.FromSeconds(1),
                TestUtils.Policy,
                TestUtils.Peer1Priv,
                validators);

            Assert.Equal(Step.Null, consensusContext.Step);
            Assert.Equal("No context", consensusContext.ToString());
        }

        [Fact(Timeout = Timeout)]
        public async void NewHeightWhenTipChanged()
        {
            var newHeightDelay = TimeSpan.FromSeconds(1);
            var validators = new List<PublicKey>()
            {
                TestUtils.Peer0Priv.PublicKey, TestUtils.Peer1Priv.PublicKey,
            };

            var (_, blockChain, consensusContext) = TestUtils.CreateDummyConsensusContext(
                newHeightDelay,
                TestUtils.Policy,
                TestUtils.Peer1Priv,
                validators);

            Assert.Equal(-1, consensusContext.Height);
            blockChain.Append(blockChain.ProposeBlock(new PrivateKey()));
            Assert.Equal(-1, consensusContext.Height);
            await Task.Delay(newHeightDelay + TimeSpan.FromSeconds(1));
            Assert.Equal(2, consensusContext.Height);
        }

        [Fact(Timeout = Timeout)]
        public void IgnoreMessagesFromLowerHeight()
        {
            var validators = new List<PublicKey>()
            {
                TestUtils.Peer0Priv.PublicKey, TestUtils.Peer1Priv.PublicKey,
            };

            var (fx, blockChain, consensusContext) = TestUtils.CreateDummyConsensusContext(
                TimeSpan.FromSeconds(1),
                TestUtils.Policy,
                TestUtils.Peer1Priv,
                validators);

            consensusContext.NewHeight(blockChain.Tip.Index + 1);
            Assert.True(consensusContext.Height == 1);
            Assert.Throws<InvalidConsensusMessageException>(
                () => consensusContext.HandleMessage(
                    TestUtils.CreateConsensusPropose(
                        blockChain.ProposeBlock(TestUtils.Peer0Priv),
                        TestUtils.Peer0Priv,
                        0)));
        }

        [Fact(Timeout = Timeout)]
        public async void ClearOldLastCommitCache()
        {
            var codec = new Codec();
            var heightOnePreVote = new AsyncAutoResetEvent();
            var heightOnePreCommit = new AsyncAutoResetEvent();
            var heightOneEnded = new AsyncAutoResetEvent();
            var heightTwoPreVote = new AsyncAutoResetEvent();
            var heightTwoPreCommit = new AsyncAutoResetEvent();
            var heightTwoEnded = new AsyncAutoResetEvent();
            var heightThreePropose = new AsyncAutoResetEvent();
            Block<DumbAction>? proposedBlock = null;

            var (_, blockChain, consensusContext) = TestUtils.CreateDummyConsensusContext(
                TimeSpan.FromSeconds(1),
                TestUtils.Policy,
                TestUtils.Peer1Priv,
                consensusMessageSent: CatchPropose,
                lastCommitClearThreshold: 1);

            void CatchPropose(object? sender, ConsensusMsg? message)
            {
                if (message is ConsensusProposalMsg propose)
                {
                    proposedBlock =
                        BlockMarshaler.UnmarshalBlock<DumbAction>(
                            (Dictionary)codec.Decode(propose.Proposal.MarshaledBlock));
                }
            }

            consensusContext.StateChanged += (_, eventArgs) =>
            {
                if (eventArgs.Height == 1 && eventArgs.Step == Step.PreVote)
                {
                    heightOnePreVote.Set();
                }

                if (eventArgs.Height == 1 && eventArgs.Step == Step.PreCommit)
                {
                    heightOnePreCommit.Set();
                }

                if (eventArgs.Height == 1 && eventArgs.Step == Step.EndCommit)
                {
                    heightOneEnded.Set();
                }

                if (eventArgs.Height == 2 && eventArgs.Step == Step.PreVote)
                {
                    heightTwoPreVote.Set();
                }

                if (eventArgs.Height == 2 && eventArgs.Step == Step.PreCommit)
                {
                    heightTwoPreCommit.Set();
                }

                if (eventArgs.Height == 2 && eventArgs.Step == Step.EndCommit)
                {
                    heightTwoEnded.Set();
                }

                if (eventArgs.Height == 3 && eventArgs.Step == Step.Propose)
                {
                    heightThreePropose.Set();
                }
            };
            consensusContext.MessageConsumed += (_, message) =>
            {
                if (message.Height == 2 && message.Message is ConsensusProposalMsg propose)
                {
                    proposedBlock = BlockMarshaler.UnmarshalBlock<DumbAction>(
                        (Dictionary)codec.Decode(propose!.Proposal.MarshaledBlock));
                }
            };

            // Do a consensus for height to #2 consecutively.
            consensusContext.NewHeight(blockChain.Tip.Index + 1);

            await heightOnePreVote.WaitAsync();

            TestUtils.HandleFourPeersPreVoteMessages(
                consensusContext,
                TestUtils.Peer1Priv,
                proposedBlock!.Hash);

            await heightOnePreCommit.WaitAsync();

            TestUtils.HandleFourPeersPreCommitMessages(
                consensusContext,
                TestUtils.Peer1Priv,
                proposedBlock!.Hash);

            await heightOneEnded.WaitAsync();

            // Starts NewHeight manually.
            consensusContext.NewHeight(blockChain.Tip.Index + 1);

            var block = blockChain.ProposeBlock(
                TestUtils.Peer0Priv,
                lastCommit:
                TestUtils.CreateLastCommit(blockChain.Tip.Hash, blockChain.Tip.Index, 0));
            consensusContext.HandleMessage(
                TestUtils.CreateConsensusPropose(block, TestUtils.Peer2Priv, height: 2));

            await heightTwoPreVote.WaitAsync();

            TestUtils.HandleFourPeersPreVoteMessages(
                consensusContext,
                TestUtils.Peer1Priv,
                proposedBlock!.Hash);

            await heightTwoPreCommit.WaitAsync();

            TestUtils.HandleFourPeersPreCommitMessages(
                consensusContext,
                TestUtils.Peer1Priv,
                block.Hash);

            await heightTwoEnded.WaitAsync();

            // Starts height 3, Waits PreVote timeout
            // Checks previous LastCommit and see if it's available.
            consensusContext.NewHeight(blockChain.Tip.Index + 1);
            await heightThreePropose.WaitAsync();

            Assert.NotNull(blockChain.Store.GetLastCommit(blockChain.Tip.Index));
            Assert.Null(blockChain.Store.GetLastCommit(blockChain.Tip.Index - 1));
        }

        [Fact(Timeout = Timeout)]
        public void RemoveOldContexts()
        {
            var (_, blockChain, consensusContext) = TestUtils.CreateDummyConsensusContext(
                TimeSpan.FromSeconds(1),
                TestUtils.Policy,
                TestUtils.Peer1Priv,
                TestUtils.Validators,
                lastCommitClearThreshold: 1);

            // Create context of index 1.
            consensusContext.NewHeight(1);
            // Create context of index 2.
            consensusContext.HandleMessage(
                TestUtils.CreateConsensusPropose(
                    blockChain.ProposeBlock(TestUtils.Peer2Priv),
                    TestUtils.Peer2Priv,
                    2,
                    1));

            blockChain.Append(blockChain.ProposeBlock(new PrivateKey()));
            blockChain.Append(
                blockChain.ProposeBlock(
                    new PrivateKey(),
                    lastCommit: TestUtils.CreateLastCommit(
                        blockChain.Tip.Hash,
                        blockChain.Tip.Index,
                        0)));
            blockChain.Append(
                blockChain.ProposeBlock(
                    new PrivateKey(),
                    lastCommit: TestUtils.CreateLastCommit(
                        blockChain.Tip.Hash,
                        blockChain.Tip.Index,
                        0)));

            // Create context of index 4, check if the context of 1 and 2 are removed correctly.
            consensusContext.NewHeight(4);
            Assert.Throws<KeyNotFoundException>(() => consensusContext.Contexts[1]);
            Assert.Throws<KeyNotFoundException>(() => consensusContext.Contexts[2]);
        }

        [Fact(Timeout = Timeout)]
        public async void VoteSetGetOnlyProposeCommitHash()
        {
            Block<DumbAction>? proposedBlock = null;
            var codec = new Codec();
            var heightOneProposeSent = new AsyncAutoResetEvent();
            var heightOneEndCommit = new AsyncAutoResetEvent();
            var votes = new List<Vote>();

            var (fx, blockChain, consensusContext) = TestUtils.CreateDummyConsensusContext(
                TimeSpan.FromSeconds(1),
                TestUtils.Policy,
                TestUtils.Peer1Priv,
                consensusMessageSent: (sender, msg) =>
                {
                    if (msg is ConsensusProposalMsg proposeMsg)
                    {
                        proposedBlock = BlockMarshaler.UnmarshalBlock<DumbAction>(
                            (Dictionary)codec.Decode(proposeMsg.Proposal.MarshaledBlock));
                        heightOneProposeSent.Set();
                    }
                });

            consensusContext.StateChanged += (sender, tuple) =>
            {
                if (tuple.Height == 1 && tuple.Step == Step.EndCommit)
                {
                    heightOneEndCommit.Set();
                }
            };

            consensusContext.NewHeight(blockChain.Tip.Index + 1);

            await heightOneProposeSent.WaitAsync();

            votes.Add(TestUtils.CreateVote(
                TestUtils.Peer0Priv,
                1,
                0,
                fx.Block1.Hash,
                VoteFlag.PreCommit));
            votes.AddRange(Enumerable.Range(1, 3).Select(x => TestUtils.CreateVote(
                TestUtils.PrivateKeys[x],
                1,
                0,
                proposedBlock!.Hash,
                VoteFlag.PreCommit)));

            foreach (var vote in votes)
            {
                consensusContext.HandleMessage(new ConsensusPreCommitMsg(vote));
            }

            await heightOneEndCommit.WaitAsync();

            var blockCommit = consensusContext.Contexts[1].GetBlockCommit();
            Assert.NotNull(blockCommit);
            Assert.NotEqual(votes[0], blockCommit!.Votes.First(x =>
                x.Validator.Equals(TestUtils.Peer0Priv.PublicKey)));

            var actualVotesWithoutInvalid =
                HashSetExtensions.ToHashSet(blockCommit.Votes.Where(x =>
                    !x.Validator.Equals(TestUtils.Peer0Priv.PublicKey)));

            var expectedVotes = HashSetExtensions.ToHashSet(votes.Where(x =>
                !x.Validator.Equals(TestUtils.Peer0Priv.PublicKey)));

            Assert.Equal(expectedVotes, actualVotesWithoutInvalid);
        }
    }
}
