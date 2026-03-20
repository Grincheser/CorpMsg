namespace CorpMsg.Services
{
    public interface IPasswordHasher
    {
        string Hash(string password);
        bool Verify(string password, string hash);
    }

    public class BCryptPasswordHasher : IPasswordHasher
    {
        public string Hash(string password) => BCrypt.Net.BCrypt.HashPassword(password);
        public bool Verify(string password, string hash) => BCrypt.Net.BCrypt.Verify(password, hash);
    }

    public interface IBannedWordsService
    {
        string FilterContent(string content, List<string> bannedWords);
    }

    public class BannedWordsService : IBannedWordsService
    {
        public string FilterContent(string content, List<string> bannedWords)
        {
            if (string.IsNullOrEmpty(content) || bannedWords == null || !bannedWords.Any())
                return content;

            var filtered = content;
            foreach (var word in bannedWords)
            {
                var replacement = new string('*', word.Length);
                filtered = filtered.Replace(word, replacement, StringComparison.OrdinalIgnoreCase);
            }

            return filtered;
        }
    }
}