﻿using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using CollAction.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace CollAction.Data
{
    public class ApplicationDbContext : IdentityDbContext<ApplicationUser>
    {
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
            : base(options)
        { }

        public DbSet<Project> Projects { get; set; }
        public DbSet<ProjectParticipant> ProjectParticipants { get; set; }
        public DbSet<ProjectTag> ProjectTags { get; set; }
        public DbSet<Tag> Tags { get; set; }
        public DbSet<Category> Categories { get; set; }
        public DbSet<ImageFile> ImageFiles { get; set; }
        public DbSet<VideoLink> VideoLinks { get; set; }
        public DbSet<Job> Jobs { get; set; }
        public DbSet<DataProtectionKey> DataProtectionKeys { get; set; }
        public DbSet<ProjectParticipantCount> ProjectParticipantCounts { get; set; }
        public DbSet<UserEvent> UserEvents { get; set; }
        public DbSet<DonationEventLog> DonationEventLog { get; set; }

        /// <summary>
        /// Configure the model (foreign keys, relations, primary keys, etc)
        /// </summary>
        /// <param name="builder">Model builder</param>
        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);

            builder.Entity<Tag>()
                   .HasAlternateKey(t => t.Name);
            builder.Entity<Project>()
                   .HasIndex(p => p.Name)
                   .HasName("IX_Projects_Name").IsUnique();
            builder.Entity<Project>()
                   .Property(p => p.DisplayPriority)
                   .HasDefaultValue(ProjectDisplayPriority.Medium);
            builder.Entity<ApplicationUser>()
                   .HasMany(p => p.Projects)
                   .WithOne(proj => proj.Owner)
                   .HasForeignKey(proj => proj.OwnerId)
                   .OnDelete(DeleteBehavior.SetNull);
            builder.Entity<ProjectParticipant>()
                   .HasKey("UserId", "ProjectId");
            builder.Entity<ProjectTag>()
                   .HasKey("TagId", "ProjectId");
            builder.Entity<Project>()
                   .HasOne(p => p.ParticipantCounts)
                   .WithOne(p => p.Project)
                   .HasForeignKey<ProjectParticipantCount>(p => p.ProjectId);
            builder.Entity<ApplicationUser>().Property(u => u.RepresentsNumberParticipants).HasDefaultValue(1);
        }

        /// <summary>
        /// Seed the database with initialisation data here
        /// </summary>
        /// <param name="configuration">Configuration</param>
        /// <param name="roleManager">Role managers to create and query roles</param>
        /// <param name="userManager">User manager to create and query users</param>
        /// <param name="token">Cancellation token</param>
        public async Task Seed(IConfiguration configuration, UserManager<ApplicationUser> userManager, RoleManager<IdentityRole> roleManager)
        {
            ChangeTracker.AutoDetectChangesEnabled = false;
            await CreateAdminRoleAndUser(configuration, userManager, roleManager);
            await CreateCategories();
        }

        private async Task CreateAdminRoleAndUser(IConfiguration configuration, UserManager<ApplicationUser> userManager, RoleManager<IdentityRole> roleManager)
        {
            // Create admin role if not exists
            IdentityRole adminRole = await roleManager.FindByNameAsync(Constants.AdminRole);
            if (adminRole == null)
            {
                adminRole = new IdentityRole(Constants.AdminRole) { NormalizedName = Constants.AdminRole };
                IdentityResult result = await roleManager.CreateAsync(adminRole);
                if (!result.Succeeded)
                    throw new InvalidOperationException($"Error creating role.{Environment.NewLine}{string.Join(Environment.NewLine, result.Errors.Select(e => $"{e.Code}: {e.Description}"))}");
            }

            // Create admin user if not exists
            string adminPassword = configuration["AdminPassword"];
            string adminEmail = configuration["AdminEmail"];
            ApplicationUser admin = await userManager.FindByEmailAsync(adminEmail);
            if (admin == null)
            {
                admin = new ApplicationUser(adminEmail) { EmailConfirmed = true };
                IdentityResult result = await userManager.CreateAsync(admin, adminPassword);
                if (!result.Succeeded)
                    throw new InvalidOperationException($"Error creating user.{Environment.NewLine}{string.Join(Environment.NewLine, result.Errors.Select(e => $"{e.Code}: {e.Description}"))}");
            }

            // Assign admin role if not assigned
            if (!(await userManager.IsInRoleAsync(admin, Constants.AdminRole)))
            {
                IdentityResult result = await userManager.AddToRoleAsync(admin, Constants.AdminRole);
                if (!result.Succeeded)
                    throw new InvalidOperationException($"Error assigning admin role.{Environment.NewLine}{string.Join(Environment.NewLine, result.Errors.Select(e => $"{e.Code}: {e.Description}"))}");
            }

            DetachAll();
        }

        private async Task CreateCategories()
        {
            // Initialize categories
            if (!(await Categories.AnyAsync()))
            {
                Categories.AddRange(new[] {
                    new Category() { Name = "Environment", ColorHex = "E88424", Description = "Environment", File = "" },
                    new Category() { Name = "Community", ColorHex = "7B2164", Description = "Community", File = "" },
                    new Category() { Name = "Consumption", ColorHex = "9D1D20", Description = "Consumption", File = "" },
                    new Category() { Name = "Well-being", ColorHex = "3762AE", Description = "Well-being", File = "" },
                    new Category() { Name = "Governance", ColorHex = "29ABE2", Description = "Governance", File = "" },
                    new Category() { Name = "Health", ColorHex = "EB078C", Description = "Health", File = "" },
                    new Category() { Name = "Other", ColorHex = "007D43", Description = "Other", File = "" },
                });
                await SaveChangesAsync();
                DetachAll();
            }
        }

        public void DetachAll()
        {
            foreach (EntityEntry entry in ChangeTracker.Entries().ToArray())
                if (entry.Entity != null)
                    entry.State = EntityState.Detached;
        }
    }
}
