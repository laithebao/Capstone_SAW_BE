using SAW.Infrastructure.AiChat.Rag;

namespace SAW.Test.Infrastructure.AiChat;

public sealed class DocumentChunkerTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n ")]
    public void EmptyDocumentsDoNotProduceEmbeddingChunks(string? text) => Assert.Empty(DocumentChunker.Chunk(text!));

    [Fact]
    public void NormalizationPreservesVietnameseContentAndParagraphs()
    {
        var chunks = DocumentChunker.Chunk("  Quy\t\ttrình  SAW\r\n\r\nĐặt mua lô  ").ToArray();
        Assert.Equal("Quy trình SAW\n\nĐặt mua lô", Assert.Single(chunks));
    }

    [Theory]
    [InlineData(1000, 1)]
    [InlineData(1001, 2)]
    [InlineData(1850, 2)]
    public void LongUnbrokenTextUsesOverlapAndRetainsEveryCharacter(int length, int expectedChunks)
    {
        var chunks = DocumentChunker.Chunk(new string('x', length)).ToArray();
        Assert.Equal(expectedChunks, chunks.Length);
        Assert.All(chunks, chunk => Assert.InRange(chunk.Length, 1, 1000));
        Assert.Equal(length, chunks.Sum(chunk => chunk.Length) - (chunks.Length - 1) * 150);
    }
}
