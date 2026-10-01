using AlgoTrading.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlgoTrading.Infrastructure.Persistence.Configurations;

/// <summary>EF configuration for <see cref="AiExamQuestion"/>.</summary>
public class AiExamQuestionConfiguration : IEntityTypeConfiguration<AiExamQuestion>
{
    public void Configure(EntityTypeBuilder<AiExamQuestion> builder)
    {
        builder.ToTable("ai_exam_questions", table =>
            table.HasCheckConstraint("CK_ai_exam_questions_Set", IncidentConfiguration.SqlIn("Set", AiExamSet.All)));

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Template).IsRequired().HasMaxLength(40);
        builder.Property(x => x.Subject).IsRequired().HasMaxLength(100).HasDefaultValue(string.Empty);
        builder.Property(x => x.Set).IsRequired().HasMaxLength(16);
        builder.Property(x => x.Text).IsRequired().HasMaxLength(500);
        builder.Property(x => x.Kind).IsRequired().HasMaxLength(16);
        builder.Property(x => x.Expected).IsRequired().HasMaxLength(100);

        // The bank is built a day at a time: "is this day in it yet".
        builder.HasIndex(x => new { x.Day, x.Template }, "IX_ai_exam_questions_Day_Template");
    }
}

/// <summary>EF configuration for <see cref="AiExam"/>.</summary>
public class AiExamConfiguration : IEntityTypeConfiguration<AiExam>
{
    public void Configure(EntityTypeBuilder<AiExam> builder)
    {
        builder.ToTable("ai_exams");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Trigger).IsRequired().HasMaxLength(100);
        builder.Property(x => x.QuestionIdsJson).IsRequired();
        builder.HasIndex(x => x.StartedUtc, "IX_ai_exams_StartedUtc");
    }
}

/// <summary>EF configuration for <see cref="AiExamAnswer"/>.</summary>
public class AiExamAnswerConfiguration : IEntityTypeConfiguration<AiExamAnswer>
{
    public void Configure(EntityTypeBuilder<AiExamAnswer> builder)
    {
        builder.ToTable("ai_exam_answers", table =>
            table.HasCheckConstraint("CK_ai_exam_answers_Outcome", IncidentConfiguration.SqlIn("Outcome", AiExamOutcome.All)));

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Outcome).IsRequired().HasMaxLength(16);
        builder.Property(x => x.Model).IsRequired().HasMaxLength(128).HasDefaultValue(string.Empty);
        builder.Property(x => x.Answer).IsRequired().HasMaxLength(600).HasDefaultValue(string.Empty);
        builder.Property(x => x.Truth).IsRequired().HasMaxLength(100).HasDefaultValue(string.Empty);

        // One row per ask: a restarted exam carries on where it stopped.
        builder.HasIndex(x => new { x.ExamId, x.QuestionId, x.Attempt }, "IX_ai_exam_answers_Exam_Question_Attempt").IsUnique();
        builder.HasOne<AiExam>().WithMany().HasForeignKey(x => x.ExamId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<AiExamQuestion>().WithMany().HasForeignKey(x => x.QuestionId).OnDelete(DeleteBehavior.Cascade);
    }
}
