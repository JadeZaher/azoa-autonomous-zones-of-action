using AutoMapper;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Mapping;

public class AZOAMappingProfile : Profile
{
    public AZOAMappingProfile()
    {
        // Avatar mappings
        CreateMap<AvatarRegisterModel, Avatar>();
        CreateMap<AvatarUpdateModel, Avatar>()
            .ForAllMembers(opts => opts.Condition((src, dest, srcMember) => srcMember != null));

        // Holon mappings
        CreateMap<HolonCreateModel, Holon>();
        CreateMap<HolonUpdateModel, Holon>()
            .ForAllMembers(opts => opts.Condition((src, dest, srcMember) => srcMember != null));

        // Wallet mappings
        CreateMap<WalletCreateModel, Wallet>();
        CreateMap<WalletUpdateModel, Wallet>()
            .ForAllMembers(opts => opts.Condition((src, dest, srcMember) => srcMember != null));

        // NFT / Search hit mappings
        CreateMap<IHolon, NftResult>()
            .ForMember(dest => dest.OwnerAvatarId, opt => opt.MapFrom(src => src.AvatarId));

        CreateMap<IHolon, SearchHit>()
            .ForMember(dest => dest.EntityType, opt => opt.MapFrom(_ => SearchableEntityType.Holon))
            .ForMember(dest => dest.Title, opt => opt.MapFrom(src => src.Name));

        CreateMap<IAvatar, SearchHit>()
            .ForMember(dest => dest.EntityType, opt => opt.MapFrom(_ => SearchableEntityType.Avatar))
            .ForMember(dest => dest.Title, opt => opt.MapFrom(src => src.Username));

        CreateMap<IWallet, SearchHit>()
            .ForMember(dest => dest.EntityType, opt => opt.MapFrom(_ => SearchableEntityType.Wallet))
            .ForMember(dest => dest.Title, opt => opt.MapFrom(src => src.Address));
    }
}
