// Copyright Epic Games, Inc. All Rights Reserved.
#pragma once

#include "Chaos/Array.h"
#include "Chaos/PBDParticles.h"
#include "Chaos/PBDTetConstraintsBase.h"
#include "Chaos/PerParticleRule.h"

namespace Chaos
{
class FPerParticlePBDTetConstraints : public FPerParticleRule, public FPBDTetConstraintsBase
{
	typedef FPBDTetConstraintsBase Base;
	using Base::MConstraints;

  public:
	  FPerParticlePBDTetConstraints(const FDynamicParticles& InParticles, TArray<TVec4<int32>>&& Constraints, const FReal Stiffness = (FReal)1)
	    : Base(InParticles, MoveTemp(Constraints), Stiffness)
	{
		for (int32 i = 0; i < MConstraints.Num(); ++i)
		{
			const auto& Constraint = MConstraints[i];
			int32 i1 = Constraint[0];
			int32 i2 = Constraint[1];
			int32 i3 = Constraint[2];
			int32 i4 = Constraint[3];
			if (i1 >= MParticleToConstraints.Num())
			{
				MParticleToConstraints.SetNum(i1 + 1);
			}
			if (i2 >= MParticleToConstraints.Num())
			{
				MParticleToConstraints.SetNum(i2 + 1);
			}
			if (i3 >= MParticleToConstraints.Num())
			{
				MParticleToConstraints.SetNum(i3 + 1);
			}
			if (i4 >= MParticleToConstraints.Num())
			{
				MParticleToConstraints.SetNum(i4 + 1);
			}
			MParticleToConstraints[i1].Add(i);
			MParticleToConstraints[i2].Add(i);
			MParticleToConstraints[i2].Add(i);
			MParticleToConstraints[i2].Add(i);
		}
	}
	virtual ~FPerParticlePBDTetConstraints()
	{
	}

	void Apply(FPBDParticles& InParticles, const FReal Dt, const int32 Index) const override //-V762
	{
		for (int i = 0; i < MParticleToConstraints[Index].Num(); ++i)
		{
			int32 cIndex = MParticleToConstraints[Index][i];
			const auto& Constraint = MConstraints[cIndex];
			int32 i1 = Constraint[0];
			int32 i2 = Constraint[1];
			int32 i3 = Constraint[2];
			int32 i4 = Constraint[3];
			auto grads = Base::GetGradients(InParticles, cIndex);
			auto s = Base::GetScalingFactor(InParticles, cIndex, grads);
			if (Index == i1)
			{
				InParticles.P(i1) -= s * InParticles.InvM(i1) * grads[0];
			}
			else if (Index == i2)
			{
				InParticles.P(i2) -= s * InParticles.InvM(i2) * grads[1];
			}
			else if (Index == i3)
			{
				InParticles.P(i3) -= s * InParticles.InvM(i3) * grads[2];
			}
			else
			{
				check(Index == i4);
				InParticles.P(i4) -= s * InParticles.InvM(i4) * grads[3];
			}
		}
	}

  private:
	TArray<TArray<int32>> MParticleToConstraints;
};

template<class T>
using PerParticlePBDTetConstraints UE_DEPRECATED(4.27, "Deprecated. this class is to be deleted, use FPerParticlePBDTetConstraints instead") = FPerParticlePBDTetConstraints;

}
