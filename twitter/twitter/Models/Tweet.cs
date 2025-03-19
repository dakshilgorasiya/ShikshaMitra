using System.ComponentModel.DataAnnotations.Schema;
using System.ComponentModel.DataAnnotations;

namespace twitter.Models
{
    public class Tweet
    {
        [Key]
        public int Id { get; set; }
        public string Title { get; set; }
        public string[] Tags { get; set; }
        public string TweetData { get; set; }
        public DateTime? CreatedAt { get; set; } = DateTime.UtcNow;
        public int OwnerId { get; set; }
        [ForeignKey("OwnerId")]
        public User Owner { get; set; }
        public ICollection<Comment> Comments { get; set; }
        public ICollection<Like> Likes { get; set; }
        public ICollection<Report> Reports { get; set; }
    }
}
