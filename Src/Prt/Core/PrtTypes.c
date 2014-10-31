#include "../API/PrtTypes.h"

PRT_TYPE * PRT_CALL_CONV PrtMkPrimitiveType(_In_ PRT_TYPE_KIND primType)
{
	PRT_TYPE *type = (PRT_TYPE *)PrtMalloc(sizeof(PRT_TYPE));
	type->typeUnion.map = NULL;
	switch (primType)
	{
		case PRT_KIND_ANY:
		case PRT_KIND_BOOL:
		case PRT_KIND_EVENT:
		case PRT_KIND_MACHINE:
		case PRT_KIND_INT:
		case PRT_KIND_NULL:
		{
			type->typeKind = primType;
			return type;
		}
		default:
		{
			PrtAssert(PRT_FALSE, "Expected a primitive type.");
			type->typeKind = PRT_TYPE_KIND_CANARY;
			return type;
		}
	}
}

PRT_TYPE * PRT_CALL_CONV PrtMkForgnType(
	_In_ PRT_GUID              typeTag,
	_In_ PRT_FORGN_CLONE       cloner,
	_In_ PRT_FORGN_FREE        freer,
	_In_ PRT_FORGN_GETHASHCODE hasher,
	_In_ PRT_FORGN_ISEQUAL     eqTester)
{
	PrtAssert(cloner != NULL, "Bad cloner");
	PrtAssert(freer != NULL, "Bad freer");
	PrtAssert(hasher != NULL, "Bad hasher");
	PrtAssert(eqTester != NULL, "Bad equality tester");

	PRT_TYPE *type = (PRT_TYPE *)PrtMalloc(sizeof(PRT_TYPE));
	PRT_FORGNTYPE *forgn = (PRT_FORGNTYPE *)PrtMalloc(sizeof(PRT_FORGNTYPE));
	type->typeKind = PRT_KIND_FORGN;
	type->typeUnion.forgn = forgn;

	forgn->typeTag = typeTag;
	forgn->cloner = cloner;
	forgn->freer = freer;
	forgn->hasher = hasher;
	forgn->eqTester = eqTester;

	return type;
}

PRT_TYPE * PRT_CALL_CONV PrtMkMapType(_In_ PRT_TYPE *domType, _In_ PRT_TYPE *codType)
{
	PrtAssert(PrtIsValidType(domType), "Invalid type expression");
	PrtAssert(PrtIsValidType(codType), "Invalid type expression");

	PRT_TYPE *type = (PRT_TYPE *)PrtMalloc(sizeof(PRT_TYPE));
	PRT_MAPTYPE *map = (PRT_MAPTYPE *)PrtMalloc(sizeof(PRT_MAPTYPE));
	type->typeKind = PRT_KIND_MAP;
	type->typeUnion.map = map;

	map->domType = PrtCloneType(domType);
	map->codType = PrtCloneType(codType);
	return type;
}

PRT_TYPE * PRT_CALL_CONV PrtMkNmdTupType(_In_ PRT_UINT32 arity)
{
	PrtAssert(arity > 0, "Invalid tuple arity");
	PRT_TYPE *type = (PRT_TYPE *)PrtMalloc(sizeof(PRT_TYPE));
	PRT_NMDTUPTYPE *nmdTup = (PRT_NMDTUPTYPE *)PrtMalloc(sizeof(PRT_NMDTUPTYPE));
	type->typeKind = PRT_KIND_NMDTUP;
	type->typeUnion.nmTuple = nmdTup;

	nmdTup->arity = arity;
	nmdTup->fieldNames = (PRT_STRING *)PrtCalloc((size_t)arity, sizeof(PRT_STRING));
	nmdTup->fieldTypes = (PRT_TYPE **)PrtCalloc((size_t)arity, sizeof(PRT_TYPE *));
	return type;
}

PRT_TYPE * PRT_CALL_CONV PrtMkTupType(_In_ PRT_UINT32 arity)
{
	PrtAssert(arity > 0, "Invalid tuple arity");
	PRT_TYPE *type = (PRT_TYPE *)PrtMalloc(sizeof(PRT_TYPE));
	PRT_TUPTYPE *tup = (PRT_TUPTYPE *)PrtMalloc(sizeof(PRT_TUPTYPE));
	type->typeKind = PRT_KIND_TUPLE;
	type->typeUnion.tuple = tup;

	tup->arity = arity;
	tup->fieldTypes = (PRT_TYPE **)PrtCalloc((size_t)arity, sizeof(PRT_TYPE *));
	return type;
}

PRT_TYPE * PRT_CALL_CONV PrtMkSeqType(_In_ PRT_TYPE *innerType)
{
	PrtAssert(PrtIsValidType(innerType), "Invalid type expression");
	PRT_TYPE *type = (PRT_TYPE *)PrtMalloc(sizeof(PRT_TYPE));
	PRT_SEQTYPE *seq = (PRT_SEQTYPE *)PrtMalloc(sizeof(PRT_SEQTYPE));
	type->typeKind = PRT_KIND_SEQ;
	type->typeUnion.seq = seq;
	seq->innerType = PrtCloneType(innerType);
	return type;
}

void PRT_CALL_CONV PrtSetFieldType(_Inout_ PRT_TYPE *tupleType, _In_ PRT_UINT32 index, _In_ PRT_TYPE *fieldType)
{
	PrtAssert(PrtIsValidType(tupleType), "Invalid type expression");
	PrtAssert(PrtIsValidType(fieldType), "Invalid type expression");
	PrtAssert(tupleType->typeKind == PRT_KIND_TUPLE || tupleType->typeKind == PRT_KIND_NMDTUP, "Invalid type expression");

	if (tupleType->typeKind == PRT_KIND_TUPLE)
	{		
		PrtAssert(index < tupleType->typeUnion.tuple->arity, "Invalid tuple index");
		tupleType->typeUnion.tuple->fieldTypes[index] = PrtCloneType(fieldType);
	}
	else if (tupleType->typeKind == PRT_KIND_NMDTUP)
	{
		PrtAssert(index < tupleType->typeUnion.nmTuple->arity, "Invalid tuple index");
		tupleType->typeUnion.nmTuple->fieldTypes[index] = PrtCloneType(fieldType);
	}
}

void PRT_CALL_CONV PrtSetFieldName(_Inout_ PRT_TYPE *tupleType, _In_ PRT_UINT32 index, _In_ PRT_STRING fieldName)
{
	PrtAssert(PrtIsValidType(tupleType), "Invalid type expression");
	PrtAssert(tupleType->typeKind == PRT_KIND_NMDTUP, "Invalid type expression");
	PrtAssert(fieldName != NULL && *fieldName != '\0', "Invalid field name");
	PrtAssert(index < tupleType->typeUnion.nmTuple->arity, "Invalid tuple index");

	size_t nameLen;
	PRT_STRING fieldNameClone;
	nameLen = strnlen(fieldName, PRT_MAXFLDNAME_LENGTH);
	PrtAssert(nameLen > 0 && nameLen < PRT_MAXFLDNAME_LENGTH, "Invalid field name");

	fieldNameClone = (PRT_STRING)PrtCalloc(nameLen + 1, sizeof(PRT_CHAR));
	strncpy(fieldNameClone, fieldName, nameLen);
	fieldNameClone[nameLen] = '\0';
	tupleType->typeUnion.nmTuple->fieldNames[index] = fieldNameClone;
}

PRT_BOOLEAN PRT_CALL_CONV PrtIsSubtype(_In_ PRT_TYPE *subType, _In_ PRT_TYPE *supType)
{
	PrtAssert(PrtIsValidType(subType), "Invalid type expression");
	PrtAssert(PrtIsValidType(supType), "Invalid type expression");

	PRT_TYPE_KIND subKind = subType->typeKind;
	PRT_TYPE_KIND supKind = supType->typeKind;
	switch (supKind)
	{
	case PRT_KIND_ANY:
	{
		//// Everything is a subtype of `any`.
		return PRT_TRUE;
	}
	case PRT_KIND_NULL:
	case PRT_KIND_EVENT:
	case PRT_KIND_MACHINE:
	{
		return (subKind == supKind || subKind == PRT_KIND_NULL) ? PRT_TRUE : PRT_FALSE;
	}
	case PRT_KIND_BOOL:
	case PRT_KIND_INT:
	case PRT_KIND_FORGN:
	{
		//// These types do not have any proper subtypes.
		return subKind == supKind ? PRT_TRUE : PRT_FALSE;
	}
	case PRT_KIND_MAP:
	{	
		//// Both types are maps and inner types are in subtype relationship.
		PRT_MAPTYPE *subMap;
		PRT_MAPTYPE *supMap;
		if (subKind != PRT_KIND_MAP)
		{
			return PRT_FALSE;
		}

		subMap = (PRT_MAPTYPE *)subType->typeUnion.map;
		supMap = (PRT_MAPTYPE *)supType->typeUnion.map;
		return
			PrtIsSubtype(subMap->domType, supMap->domType) &&
			PrtIsSubtype(subMap->codType, supMap->codType) ? PRT_TRUE : PRT_FALSE;
	}
	case PRT_KIND_NMDTUP:
	{
		//// Both types are named tuples with same field names, arity, and inner types are in subtype relationship.
		PRT_UINT32 i;
		PRT_NMDTUPTYPE *subNmdTup;
		PRT_NMDTUPTYPE *supNmdTup;
		if (subKind != PRT_KIND_NMDTUP)
		{
			return PRT_FALSE;
		}

		subNmdTup = (PRT_NMDTUPTYPE *)subType->typeUnion.nmTuple;
		supNmdTup = (PRT_NMDTUPTYPE *)supType->typeUnion.nmTuple;
		if (subNmdTup->arity != supNmdTup->arity)
		{
			return PRT_FALSE;
		}
		
		//// Next check field names.
		for (i = 0; i < subNmdTup->arity; ++i)
		{
			if (strncmp(subNmdTup->fieldNames[i], supNmdTup->fieldNames[i], PRT_MAXFLDNAME_LENGTH) != 0)
			{
				return PRT_FALSE;
			}
		}

		//// Finally check field types.
		for (i = 0; i < subNmdTup->arity; ++i)
		{
			if (!PrtIsSubtype(subNmdTup->fieldTypes[i], supNmdTup->fieldTypes[i]))
			{
				return PRT_FALSE;
			}
		}

		return PRT_TRUE;
	}
	case PRT_KIND_SEQ:
	{
		//// Both types are sequences and inner types are in subtype relationship.
		PRT_SEQTYPE *subSeq;
		PRT_SEQTYPE *supSeq;
		if (subKind != PRT_KIND_SEQ)
		{
			return PRT_FALSE;
		}

		subSeq = (PRT_SEQTYPE *)subType->typeUnion.seq;
		supSeq = (PRT_SEQTYPE *)supType->typeUnion.seq;
		return PrtIsSubtype(subSeq->innerType, supSeq->innerType);
	}
	case PRT_KIND_TUPLE:
	{
		//// Both types are tuples with same arity, and inner types are in subtype relationship.
		PRT_UINT32 i;
		PRT_TUPTYPE *subTup;
		PRT_TUPTYPE *supTup;
		if (subKind != PRT_KIND_TUPLE)
		{
			return PRT_FALSE;
		}

		subTup = (PRT_TUPTYPE *)subType->typeUnion.tuple;
		supTup = (PRT_TUPTYPE *)supType->typeUnion.tuple;
		if (subTup->arity != supTup->arity)
		{
			return PRT_FALSE;
		}

		//// Finally check field types.
		for (i = 0; i < subTup->arity; ++i)
		{
			if (!PrtIsSubtype(subTup->fieldTypes[i], supTup->fieldTypes[i]))
			{
				return PRT_FALSE;
			}
		}

		return PRT_TRUE;
	}
	default:
		PrtAssert(PRT_FALSE, "Invalid type");
		return PRT_FALSE;
	}
}

PRT_TYPE * PRT_CALL_CONV PrtCloneType(_In_ PRT_TYPE *type)
{
	PrtAssert(PrtIsValidType(type), "Invalid type expression");
	PRT_TYPE_KIND kind = type->typeKind;
	switch (kind)
	{
	case PRT_KIND_ANY:
	case PRT_KIND_BOOL:
	case PRT_KIND_EVENT:
	case PRT_KIND_MACHINE:
	case PRT_KIND_INT:
	case PRT_KIND_NULL:
	{
		return PrtMkPrimitiveType(kind);
	}
	case PRT_KIND_FORGN:
	{
		PRT_FORGNTYPE *ftype = type->typeUnion.forgn;
		return PrtMkForgnType(ftype->typeTag, ftype->cloner, ftype->freer, ftype->hasher, ftype->eqTester);
	}
	case PRT_KIND_MAP:
	{		
		PRT_MAPTYPE *mtype = type->typeUnion.map;
		return PrtMkMapType(mtype->domType, mtype->codType);
	}
	case PRT_KIND_NMDTUP:
	{
		PRT_UINT32 i;
		PRT_NMDTUPTYPE *ntype = type->typeUnion.nmTuple;
		PRT_TYPE *clone = PrtMkNmdTupType(ntype->arity);
		for (i = 0; i < ntype->arity; ++i)
		{
			PrtSetFieldName(clone, i, ntype->fieldNames[i]);
			PrtSetFieldType(clone, i, ntype->fieldTypes[i]);
		}

		return clone;
	}
	case PRT_KIND_SEQ:
	{
		PRT_SEQTYPE *stype = type->typeUnion.seq;
		return PrtMkSeqType(stype->innerType);
	}
	case PRT_KIND_TUPLE:
	{
		PRT_UINT32 i;
		PRT_TUPTYPE *ttype = type->typeUnion.tuple;
		PRT_TYPE *clone = PrtMkTupType(ttype->arity);
		for (i = 0; i < ttype->arity; ++i)
		{
			PrtSetFieldType(clone, i, ttype->fieldTypes[i]);
		}

		return clone;
	}
	default:
		PrtAssert(PRT_FALSE, "Invalid type");
		return PrtMkPrimitiveType(PRT_KIND_NULL);
	}
}

void PRT_CALL_CONV PrtFreeType(_Inout_ PRT_TYPE *type)
{
	PRT_TYPE_KIND kind = type->typeKind;
	switch (kind)
	{
	case PRT_KIND_ANY:
	case PRT_KIND_BOOL:
	case PRT_KIND_EVENT:
	case PRT_KIND_MACHINE:
	case PRT_KIND_INT:
	case PRT_KIND_NULL:
		type->typeKind = PRT_TYPE_KIND_CANARY;
		PrtFree(type);
		break;
	case PRT_KIND_FORGN:
	{
		PRT_FORGNTYPE *ftype = type->typeUnion.forgn;
		type->typeKind = PRT_TYPE_KIND_CANARY;
		PrtFree(ftype);
		PrtFree(type);
		break;
	}
	case PRT_KIND_MAP:
	{
		PRT_MAPTYPE *mtype = (PRT_MAPTYPE *)type->typeUnion.map;
		PrtFreeType(mtype->domType);
		PrtFreeType(mtype->codType);
		type->typeKind = PRT_TYPE_KIND_CANARY;
		PrtFree(mtype);
		PrtFree(type);
		break;
	}
	case PRT_KIND_NMDTUP:
	{
		PRT_UINT32 i;
		PRT_NMDTUPTYPE *ntype = type->typeUnion.nmTuple;
		for (i = 0; i < ntype->arity; ++i)
		{
			PrtFree(ntype->fieldNames[i]);
			PrtFreeType(ntype->fieldTypes[i]);
		}

		PrtFree(ntype->fieldNames);
		PrtFree(ntype->fieldTypes);
		type->typeKind = PRT_TYPE_KIND_CANARY;
		PrtFree(ntype);
		PrtFree(type);
		break;
	}
	case PRT_KIND_SEQ:
	{
		PRT_SEQTYPE *stype = type->typeUnion.seq;
		PrtFreeType(stype->innerType);
		type->typeKind = PRT_TYPE_KIND_CANARY;
		PrtFree(stype);
		PrtFree(type);
		break;
	}
	case PRT_KIND_TUPLE:
	{
		PRT_UINT32 i;
		PRT_TUPTYPE *ttype = type->typeUnion.tuple;
		for (i = 0; i < ttype->arity; ++i)
		{
			PrtFreeType(ttype->fieldTypes[i]);
		}

		PrtFree(ttype->fieldTypes);
		type->typeKind = PRT_TYPE_KIND_CANARY;
		PrtFree(ttype);
		PrtFree(type);
		break;
	}
	default:
		PrtAssert(PRT_FALSE, "Invalid type");
		break;
	}
}

/** The "Absent type" is a built-in foreign type used to represent the absence of a foreign value.
* The type tag of the absent type 0. No other foreign type is permitted to use this type tag.
* @param[in] typeTag A type tag.
* @returns `true` if the typeTag is 0, `false` otherwise.
*/
PRT_BOOLEAN PRT_CALL_CONV PrtIsAbsentTag(_In_ PRT_GUID typeTag)
{
	return 
		typeTag.data1 == 0 &&
		typeTag.data2 == 0 &&
		typeTag.data3 == 0 &&
		typeTag.data4 == 0 ? PRT_TRUE : PRT_FALSE;
}

/** The "Absent type" is a built-in foreign type used to represent the absence of a foreign value.
* The absent type has a single value, which is NULL.
* @param[in] typeTag The type tag of the absent type is always 0.
* @param[in[ frgnVal The frgnVal must be NULL.
* @returns NULL.
*/
void * PRT_CALL_CONV PrtAbsentTypeClone(_In_ PRT_GUID typeTag, _In_ void *frgnVal)
{
	PrtAssert(PrtIsAbsentTag(typeTag), "Expected the absent type");
	PrtAssert(frgnVal != NULL, "Expected the absent value");
	return NULL;
}

/** The "Absent type" is a built-in foreign type used to represent the absence of a foreign value.
* The absent type has a single value, which is NULL. Does nothing.
* @param[in] typeTag The type tag of the absent type is always 0.
* @param[in[ frgnVal The frgnVal must be NULL.
*/
void PRT_CALL_CONV PrtAbsentTypeFree(_In_ PRT_GUID typeTag, _Inout_ void *frgnVal)
{
	PrtAssert(PrtIsAbsentTag(typeTag), "Expected the absent type");
	PrtAssert(frgnVal != NULL, "Expected the absent value");
}

/** The "Absent type" is a built-in foreign type used to represent the absence of a foreign value.
* The absent type has a single value, which is NULL.
* @param[in] typeTag The type tag of the absent type is always 0.
* @param[in[ frgnVal The frgnVal must be NULL.
* @returns 0.
*/
PRT_UINT32 PRT_CALL_CONV PrtAbsentTypeGetHashCode(_In_ PRT_GUID typeTag, _In_ void *frgnVal)
{
	PrtAssert(PrtIsAbsentTag(typeTag), "Expected the absent type");
	PrtAssert(frgnVal != NULL, "Expected the absent value");
	return 0;
}

/** The "Absent type" is a built-in foreign type used to represent the absence of a foreign value.
* The absent type has a single value, which is NULL. One of the inputs must be `NULL : Absent`.
* @param[in] typeTag1 The type tag of the first foreign value.
* @param[in] frgnVal1 A pointer to the first foreign data.
* @param[in] typeTag2 The type tag of the second foreign value.
* @param[in] frgnVal2 A pointer to the second foreign data.
* @returns `true` if both inputs are absent, `false` otherwise.
*/
PRT_BOOLEAN PRT_CALL_CONV PrtAbsentTypeIsEqual(
	_In_ PRT_GUID typeTag1,
	_In_ void *frgnVal1,
	_In_ PRT_GUID typeTag2,
	_In_ void *frgnVal2)
{
	PrtAssert(PrtIsAbsentTag(typeTag1) || PrtIsAbsentTag(typeTag2), "Expected an absent value");
	PrtAssert(!PrtIsAbsentTag(typeTag1) || frgnVal1 == NULL, "Invalid absent value");
	PrtAssert(!PrtIsAbsentTag(typeTag2) || frgnVal2 == NULL, "Invalid absent value");
	if (PrtIsAbsentTag(typeTag1))
	{
		return PrtIsAbsentTag(typeTag2) ? PRT_TRUE : PRT_FALSE;
	}
	else if (PrtIsAbsentTag(typeTag2))
	{
		return PrtIsAbsentTag(typeTag1) ? PRT_TRUE : PRT_FALSE;
	}

	return PRT_FALSE;
}

/** The "Absent type" is a built-in foreign type used to represent the absence of a foreign value.
* This function constructs an instance of the absent type.
*/
PRT_TYPE * PRT_CALL_CONV PrtMkAbsentType()
{
	PRT_TYPE *type = (PRT_TYPE *)PrtMalloc(sizeof(PRT_TYPE));
	PRT_FORGNTYPE *forgn = (PRT_FORGNTYPE *)PrtMalloc(sizeof(PRT_FORGNTYPE));
	type->typeKind = PRT_KIND_FORGN;
	type->typeUnion.forgn = forgn;

	forgn->typeTag.data1 = 0;
	forgn->typeTag.data2 = 0;
	forgn->typeTag.data3 = 0;
	forgn->typeTag.data4 = 0;
	forgn->cloner = &PrtAbsentTypeClone;
	forgn->freer = &PrtAbsentTypeFree;
	forgn->hasher = &PrtAbsentTypeGetHashCode;
	forgn->eqTester = &PrtAbsentTypeIsEqual;

	return type;
}

PRT_BOOLEAN PRT_CALL_CONV PrtIsValidType(_In_ PRT_TYPE *type)
{
	if (type == NULL)
	{
		return PRT_FALSE;
	}

	PRT_TYPE_KIND kind = type->typeKind;
	switch (kind)
	{
		case PRT_KIND_ANY:
		case PRT_KIND_BOOL:
		case PRT_KIND_EVENT:
		case PRT_KIND_MACHINE:
		case PRT_KIND_INT:
		case PRT_KIND_NULL:
			return PRT_TRUE;
		case PRT_KIND_FORGN:
		{
			PRT_FORGNTYPE *forgn = type->typeUnion.forgn;
			return forgn != NULL &&
				forgn->cloner != NULL &&
				forgn->eqTester != NULL &&
				forgn->freer != NULL &&
				forgn->hasher != NULL;
		}
		case PRT_KIND_MAP:
		{
			PRT_MAPTYPE *map = type->typeUnion.map;
			return map != NULL &&
				map->codType != NULL &&
				map->domType != NULL;
		}
		case PRT_KIND_SEQ:
		{
			PRT_SEQTYPE *seq = type->typeUnion.seq;
			return seq != NULL && seq->innerType != NULL;
		}
		case PRT_KIND_TUPLE:
		{
			PRT_TUPTYPE *tup = type->typeUnion.tuple;
			return tup != NULL && tup->arity > 0 && tup->fieldTypes != NULL;
		}
		case PRT_KIND_NMDTUP:
		{
			PRT_NMDTUPTYPE *tup = type->typeUnion.nmTuple;
			return tup != NULL && tup->arity > 0 && tup->fieldTypes != NULL && tup->fieldNames != NULL;
		}
		default:
			return PRT_FALSE;
	}
}