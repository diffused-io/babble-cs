﻿using System;
using System.Collections.Generic;
using System.Linq;
using Dotnatter.Common;
using Dotnatter.Util;

namespace Dotnatter.HashgraphImpl
{
    public class Hashgraph
    {
        public Dictionary<string, int> Participants { get; set; } //[public key] => id
        public Dictionary<int, string> ReverseParticipants { get; set; } //[id] => public key
        public IStore Store { get; set; } //store of Events and Rounds
        public List<string> UndeterminedEvents { get; set; } //[index] => hash
        public Queue<int> UndecidedRounds { get; set; } //queue of Rounds which have undecided witnesses
        public int LastConsensusRound { get; set; } //index of last round where the fame of all witnesses has been decided
        public int LastCommitedRoundEvents { get; set; } //number of evs in round before LastConsensusRound
        public int ConsensusTransactions { get; set; } //number of consensus transactions
        public int PendingLoadedEvents { get; set; } //number of loaded evs that are not yet committed
        public Channel<Event> CommitChannel { get; set; } //channel for committing evs
        public int topologicalIndex { get; set; } //counter used to order evs in topological order
        public int superMajority { get; set; }

        public LruCache<string, bool> ancestorCache { get; set; }
        public LruCache<string, bool> selfAncestorCache { get; set; }
        public LruCache<string, string> oldestSelfAncestorCache { get; set; }
        public LruCache<string, bool> stronglySeeCache { get; set; }
        public LruCache<string, ParentRoundInfo> parentRoundCache { get; set; }
        public LruCache<string, int> roundCache { get; set; }

        public Hashgraph(Dictionary<string, int> participants, IStore store, Channel<Event> commitCh)
        {
            var reverseParticipants = participants.ToDictionary(p => p.Value, p => p.Key);
            var cacheSize = store.CacheSize();

            Participants = participants;
            ReverseParticipants = reverseParticipants;
            Store = store;
            CommitChannel = commitCh;
            ancestorCache = new LruCache<string, bool>(cacheSize, null);
            selfAncestorCache = new LruCache<string, bool>(cacheSize, null);
            oldestSelfAncestorCache = new LruCache<string, string>(cacheSize, null);
            stronglySeeCache = new LruCache<string, bool>(cacheSize, null);
            parentRoundCache = new LruCache<string, ParentRoundInfo>(cacheSize, null);
            roundCache = new LruCache<string, int>(cacheSize, null);
            UndeterminedEvents = new List<string>();
            superMajority = 2 * participants.Count / 3 + 1;
            UndecidedRounds = new Queue<int>(); //initialize
        }


        public int SuperMajority()
        {
            return superMajority;
        }

        //true if y is an ancestor of x
        public bool Ancestor(string x, string y)
        {
            var (c, ok) = ancestorCache.Get(Key.New(x, y));

            if (ok)
            {
                return c;
            }

            var a = AncestorInternal(x, y);
            ancestorCache.Add(Key.New(x, y), a);
            return a;
        }

        private bool AncestorInternal(string x, string y)
        {
            if (x == y)
            {
                return true;
            }

            var (ex, successx) = Store.GetEvent(x);

            if (!successx)
            {
                return false;
            }

            var (ey, successy) = Store.GetEvent(y);

            if (!successy)
            {
                return false;
            }

            var eyCreator = Participants[ey.Creator];
            var lastAncestorKnownFromYCreator = ex.LastAncestors[eyCreator].Index;

            return lastAncestorKnownFromYCreator >= ey.Index();
        }

        //true if y is a self-ancestor of x
        public bool SelfAncestor(string x, string y)
        {
            var (c, ok) = selfAncestorCache.Get(Key.New(x, y));

            if (ok)
            {
                return c;
            }
            var a = SelfAncestorInternal(x, y);
            selfAncestorCache.Add(Key.New(x, y), a);
            return a;
        }

        private bool SelfAncestorInternal(string x, string y)
        {
            if (x == y)
            {
                return true;
            }
            var (ex, successx) = Store.GetEvent(x);

            if (!successx)
            {
                return false;
            }

            var exCreator = Participants[ex.Creator];

            var (ey, successy) = Store.GetEvent(y);
            if (!successy)
            {
                return false;
            }

            var eyCreator = Participants[ey.Creator];

            return exCreator == eyCreator && ex.Index() >= ey.Index();
        }

        //true if x sees y
        public bool See(string x, string y)
        {
            return Ancestor(x, y);
            //it is not necessary to detect forks because we assume that with our
            //implementations, no two evs can be added by the same creator at the
            //same height (cf InsertEvent)
        }

        //oldest self-ancestor of x to see y
        public string OldestSelfAncestorToSee(string x, string y)
        {
            var ( c, ok) = oldestSelfAncestorCache.Get(Key.New(x, y));

            if (ok)
            {
                return c;
            }

            var res = OldestSelfAncestorToSeeInternal(x, y);
            oldestSelfAncestorCache.Add(Key.New(x, y), res);
            return res;
        }

        private string OldestSelfAncestorToSeeInternal(string x, string y)
        {
            var (ex, successx) = Store.GetEvent(x);

            if (!successx)
            {
                return "";
            }

            var (ey, successy) = Store.GetEvent(y);

            if (!successy)
            {
                return "";
            }

            var a = ey.FirstDescendants[Participants[ex.Creator]];

            if (a.Index <= ex.Index())
            {
                return a.Hash;
            }
            return "";
        }

        //true if x strongly sees y
        public bool StronglySee(string x, string y)
        {
            var (c, ok) = stronglySeeCache.Get(Key.New(x, y));
            if (ok)
            {
                return c;
            }
            var ss = StronglySeeInternal(x, y);
            stronglySeeCache.Add(Key.New(x, y), ss);
            return ss;
        }

        public bool StronglySeeInternal(string x, string y)
        {
            var (ex, successx) = Store.GetEvent(x);

            if (!successx)
            {
                return false;
            }

            var (ey, successy) = Store.GetEvent(y);

            if (!successy)
            {
                return false;
            }

            var c = 0;

            for (var i = 0; i < ex.LastAncestors.Length; i++)
            {
                if (ex.LastAncestors[i].Index >= ey.FirstDescendants[i].Index)
                {
                    c++;
                }
            }
            return c >= SuperMajority();
        }

        //PRI.round: max of parent rounds
        //PRI.isRoot: true if round is taken from a Root
        public ParentRoundInfo ParentRound(string x)
        {
            var (c, ok) = parentRoundCache.Get(x);
            if (ok)
            {
                return c;
            }
            var pr = ParentRoundInternal(x);
            parentRoundCache.Add(x, pr);

            return pr;
        }

        public ParentRoundInfo ParentRoundInternal(string x)
        {
            var res = new ParentRoundInfo();

            var (ex, successx) = Store.GetEvent(x);

            if (!successx)
            {
                return res;
            }

            //We are going to need the Root later
            var root = Store.GetRoot(ex.Creator);

            if (root == null)
            {
                return res;
            }

            var spRound = -1;

            var spRoot = false;
            //If it is the creator's first Event, use the corresponding Root
            if (ex.SelfParent() == root.X)
            {
                spRound = root.Round;
                spRoot = true;
            }
            else
            {
                spRound = Round(ex.SelfParent());

                spRoot = false;
            }

            var opRound = -1;

            var opRoot = false;

            var (_, success) = Store.GetEvent(ex.OtherParent());
            if (success)
            {
                //if we known the other-parent, fetch its Round directly
                opRound = Round(ex.OtherParent());
            }
            else if (ex.OtherParent() == root.Y)
            {
                //we do not know the other-parent but it is referenced in Root.Y
                opRound = root.Round;
                opRoot = true;
            }
            else if (root.Others.TryGetValue(x, out var other))
            {
                if (other == ex.OtherParent())
                {
                    //we do not know the other-parent but it is referenced  in Root.Others
                    //we use the Root's Round
                    //in reality the OtherParent Round is not necessarily the same as the
                    //Root's but it is necessarily smaller. Since We are intererest in the
                    //max between self-parent and other-parent rounds, this shortcut is
                    //acceptable.
                    opRound = root.Round;
                }
            }

            res.Round = spRound;
            res.IsRoot = spRoot;

            if (spRound < opRound)
            {
                res.Round = opRound;
                res.IsRoot = opRoot;
            }
            return res;
        }

        ////true if x is a witness (first ev of a round for the owner)
        public bool Witness(string x)
        {
            var (ex, successx) = Store.GetEvent(x);

            if (!successx)
            {
                return false;
            }

            var root = Store.GetRoot(ex.Creator);

            if (root == null)
            {
                return false;
            }

            //If it is the creator's first Event, return true
            if (ex.SelfParent() == root.X && ex.OtherParent() == root.Y)
            {
                return true;
            }

            return Round(x) > Round(ex.SelfParent());
        }

        //true if round of x should be incremented
        public bool RoundInc(string x)
        {
            var parentRound = ParentRound(x);

            //If parent-round was obtained from a Root, then x is the Event that sits
            //right on top of the Root. RoundInc is true.
            if (parentRound.IsRoot)
            {
                return true;
            }

            //If parent-round was obtained from a regulare Event, then we need to check
            //if x strongly-sees a strong majority of withnesses from parent-round.
            var c = 0;

            foreach (var w in Store.RoundWitnesses(parentRound.Round))
            {
                if (StronglySee(x, w))
                {
                    c++;
                }
            }

            return c >= SuperMajority();
        }

        public int RoundReceived(string x)
        {
            var (ex, successx) = Store.GetEvent(x);

            if (!successx)
            {
                return -1;
            }

            return ex.RoundReceived ?? -1;
        }

        public int Round(string x)
        {
            var (c, ok) = roundCache.Get(x);
            if (ok)
            {
                return c;
            }
            var r = RoundInternal(x);
            roundCache.Add(x, r);

            return r;
        }

        private int RoundInternal(string x)
        {
            var round = ParentRound(x).Round;

            var inc = RoundInc(x);

            if (inc)
            {
                round++;
            }
            return round;
        }

        //round(x) - round(y)
        public int RoundDiff(string x, string y)
        {
            var xRound = Round(x);

            if (xRound < 0)
            {
                throw new ApplicationException($"ev {x} has negative round");
            }
            var yRound = Round(y);

            if (yRound < 0)
            {
                throw new ApplicationException($"ev {y} has negative round");
            }

            return xRound - yRound;
        }

        public void InsertEvent(Event ev, bool setWireInfo)
        {
            //verify signature
            if (!ev.Verify())
            {
                throw new ApplicationException($"Invalid signature");
            }

            CheckSelfParent(ev);


            CheckOtherParent(ev);


            ev.TopologicalIndex = topologicalIndex;
            topologicalIndex++;


            if (setWireInfo)
            {
                SetWireInfo(ev);
            }

            InitEventCoordinates(ev);


            Store.SetEvent(ev);


            UpdateAncestorFirstDescendant(ev);


            UndeterminedEvents.Add(ev.Hex());


            if (ev.IsLoaded())
            {
                PendingLoadedEvents++;
            }
        }

        //Check the SelfParent is the Creator's last known Event
        public void CheckSelfParent(Event ev)
        {
            var selfParent = ev.SelfParent();

            var creator = ev.Creator;


            var (creatorLastKnown, _) = Store.LastFrom(creator);


            var selfParentLegit = selfParent == creatorLastKnown;


            if (!selfParentLegit)
            {
                throw new ApplicationException($"Self-parent not last known ev by creator");
            }
        }

        //Check if we know the OtherParent
        public void CheckOtherParent(Event ev)
        {
            var otherParent = ev.OtherParent();

            if (!string.IsNullOrEmpty(otherParent))
            {
                //Check if we have it
                var (_, ok) = Store.GetEvent(otherParent);

                if (!ok)
                {
                    //it might still be in the Root
                    var root = Store.GetRoot(ev.Creator);

                    if (root == null)
                    {
                        return;
                    }
                    if (root.X == ev.SelfParent() && root.Y == otherParent)
                    {
                        return;
                    }
                    var other = root.Others[ev.Hex()];

                    if (other == ev.OtherParent())
                    {
                        return;
                    }
                    throw new ApplicationException("Other-parent not known");
                }
            }
        }

        ////initialize arrays of last ancestors and first descendants
        public void InitEventCoordinates(Event ev)
        {
            var members = Participants.Count;

            ev.FirstDescendants = new EventCoordinates[members];

            for (var fakeId = 0; fakeId < members; fakeId++)
            {
                ev.FirstDescendants[fakeId] = new EventCoordinates
                {
                    Index = int.MaxValue
                };
            }

            ev.LastAncestors = new EventCoordinates[members];

            var ( selfParent, selfParentSuccess) = Store.GetEvent(ev.SelfParent());
            var ( otherParent, otherParentSuccess) = Store.GetEvent(ev.OtherParent());

            if (!selfParentSuccess && !otherParentSuccess)
            {
                for (var fakeId = 0; fakeId < members; fakeId++)
                {
                    ev.LastAncestors[fakeId] = new EventCoordinates
                    {
                        Index = -1
                    };
                }
            }
            else if (!selfParentSuccess)
            {
                Array.Copy(otherParent.LastAncestors.Take(members).ToArray(), 0, ev.LastAncestors, 0, members);
            }
            else if (!otherParentSuccess)
            {
                Array.Copy(selfParent.LastAncestors.Take(members).ToArray(), 0, ev.LastAncestors, 0, members);
            }
            else
            {
                var selfParentLastAncestors = selfParent.LastAncestors;

                var otherParentLastAncestors = otherParent.LastAncestors;

                Array.Copy(selfParentLastAncestors.Take(members).ToArray(), 0, ev.LastAncestors, 0, members);

                for (var i = 0; i < members; i++)
                {
                    if (ev.LastAncestors[i].Index < otherParentLastAncestors[i].Index)
                    {
                        {
                            ev.LastAncestors[i].Index = otherParentLastAncestors[i].Index;
                            ev.LastAncestors[i].Hash = otherParentLastAncestors[i].Hash;
                        }
                    }
                }
            }
            var index = ev.Index();

            var creator = ev.Creator;

            if (Participants.TryGetValue(creator, out var fakeCreatorId))
            {
                var hash = ev.Hex();

                ev.FirstDescendants[fakeCreatorId] = new EventCoordinates {Index = index, Hash = hash};
                ev.LastAncestors[fakeCreatorId] = new EventCoordinates {Index = index, Hash = hash};
            }
        }

//update first decendant of each last ancestor to point to ev
        public void UpdateAncestorFirstDescendant(Event ev)
        {
            var fakeCreatorID = Participants[ev.Creator];

            var index = ev.Index();
            var hash = ev.Hex();

            for (var i = 0; i < ev.LastAncestors.Length; i++)
            {
                var ah = ev.LastAncestors[i]?.Hash;

                while (!string.IsNullOrEmpty(ah))
                {
                    var (a, success) = Store.GetEvent(ah);


                    if (a.FirstDescendants[fakeCreatorID].Index == int.MaxValue)
                    {
                        a.FirstDescendants[fakeCreatorID] = new EventCoordinates
                        {
                            Index = index,
                            Hash = hash
                        };

                        Store.SetEvent(a);

                        ah = a.SelfParent();
                    }
                    else
                    {
                        break;
                    }
                }
            }
        }

        public void SetWireInfo(Event ev)
        {
            var selfParentIndex = -1;

            var otherParentCreatorID = -1;

            var otherParentIndex = -1;

            var (lf, isRoot) = Store.LastFrom(ev.Creator);
            //could be the first Event inserted for this creator. In this case, use Root
            if (isRoot && lf == ev.SelfParent())
            {
                var root = Store.GetRoot(ev.Creator);

                if (root == null)
                {
                    return;
                }
                selfParentIndex = root.Index;
            }
            else
            {
                var (selfParent, ok) = Store.GetEvent(ev.SelfParent());

                if (!ok)
                {
                    return;
                }
                selfParentIndex = selfParent.Index();
            }

            if (!string.IsNullOrEmpty(ev.OtherParent()))
            {
                var (otherParent, ok) = Store.GetEvent(ev.OtherParent());

                if (!ok)
                {
                    return;
                }
                otherParentCreatorID = Participants[otherParent.Creator];

                otherParentIndex = otherParent.Index();
            }

            ev.SetWireInfo(selfParentIndex,
                otherParentCreatorID,
                otherParentIndex,
                Participants[ev.Creator]);
        }

        public Event ReadWireInfo(WireEvent wev)
        {
            var selfParent = "";
            var otherParent = "";

            var creator = ReverseParticipants[wev.Body.CreatorId];
            var creatorBytes = creator.Substring(2).StringToBytes();

            if (wev.Body.SelfParentIndex >= 0)
            {
                selfParent = Store.ParticipantEvent(creator, wev.Body.SelfParentIndex);
            }

            if (wev.Body.OtherParentIndex >= 0)
            {
                var otherParentCreator = ReverseParticipants[wev.Body.OtherParentCreatorId];
                otherParent = Store.ParticipantEvent(otherParentCreator, wev.Body.OtherParentIndex);
            }

            var body = new EventBody
            {
                Transactions = wev.Body.Transactions,
                Parents = new[] {selfParent, otherParent},
                Creator = creatorBytes,
                Timestamp = wev.Body.Timestamp,
                Index = wev.Body.Index,
                SelfParentIndex = wev.Body.SelfParentIndex,
                OtherParentCreatorId = wev.Body.OtherParentCreatorId,
                OtherParentIndex = wev.Body.OtherParentIndex,
                CreatorId = wev.Body.CreatorId
            };

            var ev = new Event
            {
                Body = body,
                Signiture = wev.Signiture
            };

            return ev;
        }

        //public void DivideRounds()
        //{
        //	for _, hash = range UndeterminedEvents
        //{
        //    roundNumber = Round(hash)
        //		witness = Witness(hash)
        //		roundInfo, err = Store.GetRound(roundNumber)

        //		//If the RoundInfo is not found in the Store's Cache, then the Hashgraph
        //		//is not aware of it yet. We need to add the roundNumber to the queue of
        //		//undecided rounds so that it will be processed in the other consensus
        //		//methods
        //		if err != nil && !common.Is(err, common.KeyNotFound) {
        //        return err

        //        }
        //		//If the RoundInfo is actually taken from the Store's DB, then it still
        //		//has not been processed by the Hashgraph consensus methods (The 'queued'
        //		//field is not exported and therefore not persisted in the DB).
        //		//RoundInfos taken from the DB directly will always have this field set
        //		//to false
        //		if !roundInfo.queued
        //    {
        //        UndecidedRounds = append(UndecidedRounds, roundNumber)

        //            roundInfo.queued = true

        //        }

        //    roundInfo.AddEvent(hash, witness)
        //		err = Store.SetRound(roundNumber, roundInfo)
        //		if err != nil
        //    {
        //        return err

        //        }
        //}
        //	return nil
        //}

        ////decide if witnesses are famous
        //public void DecideFame()
        //{
        //	votes = make(map[string](map[string]bool)) //[x][y]=>vote(x,y)

        //	decidedRounds = map[int] int{} // [round number] => index in UndecidedRounds
        //	defer updateUndecidedRounds(decidedRounds)

        //	for pos, i = range UndecidedRounds
        //{
        //    roundInfo, err = Store.GetRound(i)
        //		if err != nil
        //    {
        //        return err

        //        }
        //		for _, x = range roundInfo.Witnesses() {
        //        if roundInfo.IsDecided(x) {
        //            continue

        //            }
        //        X:
        //        for j = i + 1; j <= Store.LastRound(); j++ {
        //            for _, y = range Store.RoundWitnesses(j) {
        //                diff= j - i

        //                    if diff == 1 {
        //                    setVote(votes, y, x, See(y, x))

        //                    }
        //                else
        //                {
        //                    //count votes
        //                    ssWitnesses= []string{ }
        //                    for _, w = range Store.RoundWitnesses(j - 1) {
        //                        if StronglySee(y, w) {
        //                            ssWitnesses = append(ssWitnesses, w)

        //                            }
        //                    }
        //                    yays= 0

        //                        nays= 0

        //                        for _, w = range ssWitnesses {
        //                        if votes[w][x] {
        //                            yays++

        //                            }
        //                        else
        //                        {
        //                            nays++

        //                            }
        //                    }
        //                    v= false

        //                        t= nays

        //                        if yays >= nays {
        //                        v = true

        //                            t = yays

        //                        }

        //                    //normal round
        //                    if matMod(float64(diff), float64(len(Participants))) > 0 {
        //                        if t >= SuperMajority() {
        //                            roundInfo.SetFame(x, v)

        //                                setVote(votes, y, x, v)

        //                                break X //break out of j loop

        //                            }
        //                        else
        //                        {
        //                            setVote(votes, y, x, v)

        //                            }
        //                    }
        //                    else
        //                    { //coin round
        //                        if t >= SuperMajority() {
        //                            setVote(votes, y, x, v)

        //                            }
        //                        else
        //                        {
        //                            setVote(votes, y, x, middleBit(y)) //middle bit of y's hash

        //                            }
        //                    }
        //                }
        //            }
        //        }
        //    }

        //		//Update decidedRounds and LastConsensusRound if all witnesses have been decided
        //		if roundInfo.WitnessesDecided() {
        //        decidedRounds[i] = pos


        //            if LastConsensusRound == nil || i > *LastConsensusRound {
        //            setLastConsensusRound(i)

        //            }
        //    }

        //    err = Store.SetRound(i, roundInfo)
        //		if err != nil
        //    {
        //        return err

        //        }
        //}
        //	return nil
        //}

        ////remove items from UndecidedRounds
        //public void updateUndecidedRounds(decidedRounds map[int]int)
        //{
        //    newUndecidedRounds= []int{ }
        //    for _, ur = range UndecidedRounds {
        //        if _, ok= decidedRounds[ur]; !ok {
        //            newUndecidedRounds = append(newUndecidedRounds, ur)

        //        }
        //    }
        //    UndecidedRounds = newUndecidedRounds
        //}

        //public void setLastConsensusRound(i int)
        //{
        //    if LastConsensusRound == nil {
        //        LastConsensusRound = new(int)

        //    }
        //    *LastConsensusRound = i


        //    LastCommitedRoundEvents = Store.RoundEvents(i - 1)
        //}

        ////assign round received and timestamp to all evs
        //public void DecideRoundReceived()
        //{
        //	for _, x = range UndeterminedEvents
        //{
        //    r = Round(x)
        //		for i = r + 1; i <= Store.LastRound(); i++ {
        //        tr, err= Store.GetRound(i)

        //            if err != nil && !common.Is(err, common.KeyNotFound) {
        //            return err

        //            }

        //        //skip if some witnesses are left undecided
        //        if !(tr.WitnessesDecided() && UndecidedRounds[0] > i) {
        //            continue

        //            }

        //        fws= tr.FamousWitnesses()
        //            //set of famous witnesses that see x
        //        s= []string{ }
        //        for _, w = range fws {
        //            if See(w, x) {
        //                s = append(s, w)

        //                }
        //        }
        //        if len(s) > len(fws) / 2 {
        //            ex, err= Store.GetEvent(x)

        //                if err != nil {
        //                return err

        //                }
        //            ex.SetRoundReceived(i)


        //                t= []string{ }
        //            for _, a = range s {
        //                t = append(t, OldestSelfAncestorToSee(a, x))

        //                }

        //            ex.consensusTimestamp = MedianTimestamp(t)


        //                err = Store.SetEvent(ex)

        //                if err != nil {
        //                return err

        //                }

        //            break

        //            }
        //    }
        //}
        //	return nil
        //}

        //public void FindOrder()
        //{
        //	err = DecideRoundReceived()
        //	if err != nil {
        //		return err
        //	}

        //	newConsensusEvents = [] Event{}
        //	newUndeterminedEvents = [] string{}
        //	for _, x = range UndeterminedEvents
        //{
        //    ex, err = Store.GetEvent(x)
        //		if err != nil
        //    {
        //        return err

        //        }
        //		if ex.roundReceived != nil
        //    {
        //        newConsensusEvents = append(newConsensusEvents, ex)

        //        } else {
        //        newUndeterminedEvents = append(newUndeterminedEvents, x)

        //        }
        //}
        //UndeterminedEvents = newUndeterminedEvents

        //sorter = NewConsensusSorter(newConsensusEvents)

        //    sort.Sort(sorter)

        //	for _, e = range newConsensusEvents
        //{
        //    err = Store.AddConsensusEvent(e.Hex())
        //		if err != nil
        //    {
        //        return err

        //        }
        //    ConsensusTransactions += len(e.Transactions())
        //		if e.IsLoaded() {
        //        PendingLoadedEvents--

        //        }
        //}

        //	if commitCh != nil && len(newConsensusEvents) > 0 {
        //		commitCh<- newConsensusEvents
        //	}

        //	return nil
        //}

        //public DateTime MedianTimestamp(evHashes[]string)
        //{
        //	evs = [] Event{}
        //	for _, x = range evHashes
        //{
        //    ex, _ = Store.GetEvent(x)
        //		evs = append(evs, ex)
        //	}
        //sort.Sort(ByTimestamp(evs))
        //	return evs[len(evs) / 2].Body.Timestamp
        //}

        //public void ConsensusEvents() [] string {
        //	return Store.ConsensusEvents()
        //}

        ////number of evs per participants
        //public Dictionary<int,int> Known()
        //{
        //	return Store.Known()
        //}

        //public void Reset(roots map[string]Root)
        //{
        //	if err = Store.Reset(roots); err != nil {
        //		return err
        //	}

        //	UndeterminedEvents = [] string{}
        //	UndecidedRounds = [] int{}
        //	PendingLoadedEvents = 0
        //	topologicalIndex = 0

        //	cacheSize = Store.CacheSize()
        //    ancestorCache = common.NewLRU(cacheSize, nil)
        //    selfAncestorCache = common.NewLRU(cacheSize, nil)

        //    oldestSelfAncestorCache = common.NewLRU(cacheSize, nil)

        //    stronglySeeCache = common.NewLRU(cacheSize, nil)

        //    parentRoundCache = common.NewLRU(cacheSize, nil)

        //    roundCache = common.NewLRU(cacheSize, nil)

        //	return nil
        //}

        //public Frame GetFrame()
        //{
        //	lastConsensusRoundIndex = 0
        //	if lcr = LastConsensusRound; lcr != nil {
        //		lastConsensusRoundIndex = * lcr
        //	}

        //	lastConsensusRound, err = Store.GetRound(lastConsensusRoundIndex)
        //	if err != nil {
        //		return Frame{}, err
        //	}

        //	witnessHashes = lastConsensusRound.Witnesses()

        //    evs = [] Event{}
        //	roots = make(map[string] Root)
        //	for _, wh = range witnessHashes
        //{
        //    w, err = Store.GetEvent(wh)
        //		if err != nil
        //    {
        //        return Frame{ }, err

        //        }
        //    evs = append(evs, w)
        //		roots [w.Creator()] = Root
        //    {
        //        X: w.SelfParent(),
        //			Y: w.OtherParent(),
        //			Index: w.Index() - 1,
        //			Round: Round(w.SelfParent()),
        //			Others: map[string]string{ },
        //		}

        //    participantEvents, err = Store.ParticipantEvents(w.Creator(), w.Index())
        //		if err != nil
        //    {
        //        return Frame{ }, err

        //        }
        //		for _, e = range participantEvents
        //    {
        //        ev, err= Store.GetEvent(e)

        //            if err != nil {
        //            return Frame{ }, err

        //            }
        //        evs = append(evs, ev)

        //        }
        //}

        //	//Not every participant necessarily has a witness in LastConsensusRound.
        //	//Hence, there could be participants with no Root at this point.
        //	//For these partcipants, use their last known Event.
        //	for p = range Participants
        //{
        //		if _, ok = roots [p]; !ok
        //    {
        //        var root Root
        //        last, isRoot, err = Store.LastFrom(p)

        //            if err != nil {
        //            return Frame{ }, err

        //            }
        //        if isRoot {
        //            root, err = Store.GetRoot(p)

        //                if err != nil {
        //                return Frame{ }, err

        //                }
        //        }
        //        else
        //        {
        //            ev, err= Store.GetEvent(last)

        //                if err != nil {
        //                return Frame{ }, err

        //                }
        //            evs = append(evs, ev)

        //                root = Root{
        //                X: ev.SelfParent(),
        //					Y: ev.OtherParent(),
        //					Index: ev.Index() - 1,
        //					Round: Round(ev.SelfParent()),
        //					Others: map[string]string{ },
        //				}
        //        }
        //        roots[p] = root

        //        }
        //}

        //sort.Sort(ByTopologicalOrder(evs))

        //	//Some Events in the Frame might have other-parents that are outside of the
        //	//Frame (cf root.go ex 2)
        //	//When inserting these Events in a newly reset hashgraph, the CheckOtherParent
        //	//method would return an error because the other-parent would not be found.
        //	//So we make it possible to also look for other-parents in the creator's Root.
        //	treated = map[string] bool{}
        //	for _, ev = range evs
        //{
        //    treated [ev.Hex()] = true
        //		otherParent = ev.OtherParent()
        //		if otherParent != "" {
        //        opt, ok= treated[otherParent]

        //            if !opt || !ok {
        //            if ev.SelfParent() != roots[ev.Creator()].X {
        //                roots[ev.Creator()].Others[ev.Hex()] = otherParent

        //                }
        //        }
        //    }
        //}

        //frame = Frame{
        //		Roots:  roots,
        //		Events: evs,
        //	}

        //	return frame, nil
        //}

        ////Bootstrap loads all Events from the Store's DB (if there is one) and feeds
        ////them to the Hashgraph (in topological order) for consensus ordering. After this
        ////method call, the Hashgraph should be in a state coeherent with the 'tip' of the
        ////Hashgraph
        //public void Bootstrap()
        //{
        //	if badgerStore, ok = Store.(* BadgerStore); ok {
        //		//Retreive the Events from the underlying DB. They come out in topological
        //		//order
        //		topologicalEvents, err = badgerStore.dbTopologicalEvents()
        //		if err != nil {
        //			return err
        //		}

        //		//Insert the Events in the Hashgraph
        //		for _, e = range topologicalEvents
        //{
        //			if err = InsertEvent(e, true); err != nil
        //    {
        //        return err

        //            }
        //}

        //		//Compute the consensus order of Events
        //		if err = DivideRounds(); err != nil {
        //			return err
        //		}
        //		if err = DecideFame(); err != nil {
        //			return err
        //		}
        //		if err = FindOrder(); err != nil {
        //			return err
        //		}
        //	}

        //	return nil
        //}

        //public bool middleBit(ehex string) bool {
        //	hash, err = hex.DecodeString(ehex[2:])
        //	if err != nil {
        //		fmt.Printf("ERROR decoding hex string: %s\n", err)
        //	}
        //	if len(hash) > 0 && hash[len(hash) / 2] == 0 {
        //		return false
        //	}
        //	return true
        //}

        //public void setVote(votes map[string]map[string]bool, x, y string, vote bool)
        //{
        //    if votes[x] == nil {
        //        votes[x] = make(map[string]bool)

        //    }
        //    votes[x][y] = vote
        //}
    }
}