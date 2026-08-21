# ADR 0001 — Build the inverted index rather than embed Lucene.NET

**Status:** Accepted

## Context

The system needs full-text retrieval with BM25 relevance, phrase and fuzzy matching, faceting and
highlighting. Two credible routes exist: implement the index, or host a Lucene.NET index per shard
and implement only the distributed layer around it.

## Decision

Implement the inverted index, scoring, and query execution in this repository. Take third-party
code only where reimplementation would add no insight — currently just `Porter2Stemmer` for the
Snowball English stemming algorithm.

## Alternatives considered

**Lucene.NET 4.8 per shard.** Rejected on two grounds. Its current release is a beta, which is
awkward to depend on for the core of a system. More importantly it would reduce the project to glue
code: the interesting parts — postings encoding, top-k pruning, distributed scoring — would all sit
behind someone else's API.

**Fully from scratch, no dependencies at all.** Rejected as false purity. Reimplementing a published
stemming algorithm demonstrates nothing that the rest of the engine does not already demonstrate.

## Consequences

- Full control over the segment format, the scorer, and the pruning strategy, which is what makes
  the distributed behaviour (per-shard versus global IDF, approximate facet accounting) explicit
  rather than inherited.
- Considerably more code to write and test. Correctness is defended by property tests that assert
  the optimised path agrees with an exhaustive reference implementation — see the WAND and fuzzy
  automaton tests.
- Features Lucene would have supplied for free (rich analyzers, sophisticated highlighters, FST
  suggesters) exist here in deliberately simpler form.
