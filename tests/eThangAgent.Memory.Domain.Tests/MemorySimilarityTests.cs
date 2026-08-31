namespace eThangAgent.MemoryDomain.Tests;

public class MemorySimilarityTests
{
  [Fact]
  public void IdenticalText_SimilarityOne()
  {
    double similarity = MemorySimilarity.Jaccard("prefer explicit over implicit", "prefer explicit over implicit");
    Assert.Equal(1.0, similarity, precision: 5);
  }

  [Fact]
  public void CaseAndPunctuationIgnored_SimilarityOne()
  {
    double similarity = MemorySimilarity.Jaccard("Prefer EXPLICIT over implicit!", "prefer explicit over implicit");
    Assert.Equal(1.0, similarity, precision: 5);
  }

  [Fact]
  public void DisjointTexts_SimilarityZero()
  {
    double similarity = MemorySimilarity.Jaccard("alpha beta gamma", "delta epsilon zeta");
    Assert.Equal(0.0, similarity, precision: 5);
  }

  [Fact]
  public void Subset_SharingThreeOfFour_IsExactlyThreshold()
  {
    // Existing {alpha,beta,gamma,delta}; new {alpha,beta,gamma}: I=3, U=4, J=0.75.
    double similarity = MemorySimilarity.Jaccard("alpha beta gamma delta", "alpha beta gamma");
    Assert.Equal(0.75, similarity, precision: 5);
  }

  [Fact]
  public void EmptyVersusEmpty_IsZero_NeverThrows()
  {
    double similarity = MemorySimilarity.Jaccard("!!!", "???");
    Assert.Equal(0.0, similarity, precision: 5);
  }

  [Fact]
  public void EmptyVersusNonEmpty_IsZero()
  {
    double similarity = MemorySimilarity.Jaccard("!!!", "alpha beta");
    Assert.Equal(0.0, similarity, precision: 5);
  }
}
