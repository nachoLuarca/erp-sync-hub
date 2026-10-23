using ErpSyncHub.Domain.Customers;
using ErpSyncHub.Domain.Mappings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpSyncHub.Infrastructure.Persistence.Configurations;

internal sealed class ProductMappingConfiguration : IEntityTypeConfiguration<ProductMapping>
{
    public void Configure(EntityTypeBuilder<ProductMapping> builder)
    {
        builder.ToTable("product_mappings");

        builder.HasKey(mapping => mapping.Id);
        builder.Property(mapping => mapping.Id).ValueGeneratedNever();

        builder.Property(mapping => mapping.Sku).HasMaxLength(ProductMapping.MaxSkuLength).IsRequired();
        builder.Property(mapping => mapping.ItemCode).HasMaxLength(ProductMapping.MaxItemCodeLength).IsRequired();

        builder.HasIndex(mapping => mapping.Sku).IsUnique().HasDatabaseName("ux_product_mappings_sku");
    }
}

internal sealed class CustomerMappingConfiguration : IEntityTypeConfiguration<CustomerMapping>
{
    public void Configure(EntityTypeBuilder<CustomerMapping> builder)
    {
        builder.ToTable("customer_mappings");

        builder.HasKey(mapping => mapping.Id);
        builder.Property(mapping => mapping.Id).ValueGeneratedNever();

        builder.Property(mapping => mapping.ExternalKey).HasMaxLength(CustomerMapping.MaxExternalKeyLength).IsRequired();
        builder.Property(mapping => mapping.CardCode).HasMaxLength(SapCardCodes.MaxLength).IsRequired();

        builder.HasIndex(mapping => mapping.ExternalKey).IsUnique().HasDatabaseName("ux_customer_mappings_external_key");
    }
}
