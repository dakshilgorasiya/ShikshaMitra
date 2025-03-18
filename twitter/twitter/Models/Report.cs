using System.ComponentModel.DataAnnotations.Schema;
using System.ComponentModel.DataAnnotations;

namespace twitter.Models
{
    public class Report
    {
        [Key]
        public int Id { get; set; }
        public string Title { get; set; }
        public string Content { get; set; }
        public bool IsSolved { get; set; } = false;
        public int TweetId { get; set; }
        [ForeignKey("TweetId")]
        public Tweet Tweet { get; set; }
        public int OwnerId { get; set; }
        [ForeignKey("OwnerId")]
        public User Owner { get; set; }
    }
}
