// Copyright Epic Games, Inc. All Rights Reserved.

#include "Engine/MeshSimplificationSettings.h"
#include "Modules/ModuleManager.h"
#include "UObject/UnrealType.h"

UMeshSimplificationSettings::UMeshSimplificationSettings(const FObjectInitializer& ObjectInitializer)
	: Super(ObjectInitializer)
{

}

FName UMeshSimplificationSettings::GetContainerName() const
{
	static const FName ContainerName("Project");
	return ContainerName;
}

FName UMeshSimplificationSettings::GetCategoryName() const
{
	static const FName EditorCategoryName("Editor");
	return EditorCategoryName;
}

void UMeshSimplificationSettings::PostInitProperties()
{
	Super::PostInitProperties(); 

#if WITH_EDITOR
	if(IsTemplate())
	{
		FModuleManager::Get().LoadModule("MeshReductionInterface");
		ImportConsoleVariableValues();
	}
#endif
}

#if WITH_EDITOR


void UMeshSimplificationSettings::PostEditChangeProperty(struct FPropertyChangedEvent& PropertyChangedEvent)
{
	Super::PostEditChangeProperty(PropertyChangedEvent);

	if(PropertyChangedEvent.Property)
	{
		ExportValuesToConsoleVariables(PropertyChangedEvent.Property);
	}
}


#endif
